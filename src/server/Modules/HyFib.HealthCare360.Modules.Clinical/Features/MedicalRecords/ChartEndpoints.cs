using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

/// <summary>
/// /api/v1/mrd — chart tracking, deficiencies, archival/retention (MRD-03/05), and record requests (MRD-04) with the copies
/// issued as documents and their certificates under the Bharatiya Sakshya Adhiniyam 2023, s.63.
/// </summary>
internal static class ChartEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/mrd").WithTags("Medical Records");

        group.MapGet("/records", List).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapPost("/records", Create).RequirePermission(ClinicalPermissions.MrdRecords).WithValidation<CreateMedicalRecordRequest>();
        group.MapGet("/records/{id:guid}", Get).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapPost("/records/{id:guid}/deficiencies", AddDeficiency).RequirePermission(ClinicalPermissions.MrdRecords).WithValidation<AddDeficiencyRequest>();
        group.MapPost("/records/deficiencies/{deficiencyId:guid}/resolve", ResolveDeficiency)
            .RequireAnyPermission(ClinicalPermissions.MrdRecords, ClinicalPermissions.EmrWrite).WithValidation<ResolveDeficiencyRequest>();
        group.MapPost("/records/{id:guid}/scan", Scan).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapPost("/records/{id:guid}/complete", Complete).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapPost("/records/{id:guid}/archive", Archive).RequirePermission(ClinicalPermissions.MrdRecords).WithValidation<ArchiveMedicalRecordRequest>();

        group.MapGet("/record-requests", ListRequests).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapPost("/record-requests", CreateRequest).RequirePermission(ClinicalPermissions.MrdRecords).WithValidation<CreateRecordRequestRequest>();
        group.MapGet("/record-requests/{id:guid}/documents", Documents).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapPost("/record-requests/{id:guid}/issue", Issue).RequirePermission(ClinicalPermissions.MrdRecords).WithValidation<IssueRecordRequest>();
        group.MapGet("/record-requests/{id:guid}/files/{fileId:guid}", CopyFile).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapGet("/record-requests/{id:guid}/certificate/document", Certificate).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapGet("/record-requests/{id:guid}/certificate/pdf", CertificatePdf).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapPost("/record-copies/verify", Verify).RequirePermission(ClinicalPermissions.MrdRecords).WithValidation<VerifyRecordCopyRequest>();
        group.MapPost("/record-requests/{id:guid}/return", Return).RequirePermission(ClinicalPermissions.MrdRecords);
        group.MapPost("/record-requests/{id:guid}/cancel", CancelRequest).RequirePermission(ClinicalPermissions.MrdRecords).WithValidation<CancelRecordRequest>();
    }

    /// <summary>
    /// <c>overdue</c> = incomplete charts past their due date; <c>retentionDue</c> = archived charts whose retention period has
    /// ended (candidates for review before destruction; medico-legal charts never appear).
    /// </summary>
    private static async Task<IResult> List([AsParameters] PageQuery query, MedicalRecordStatus? status, bool? overdue, bool? retentionDue,
        Guid? assignedPractitionerId, HospitalDbContext db, IFieldProtector protector, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.Today();
        var charts = db.Set<MedicalRecord>().AsNoTracking();
        if (status is not null)
        {
            charts = charts.Where(m => m.Status == status);
        }

        if (overdue == true)
        {
            charts = charts.Where(m => m.DueDate < today && (m.Status == MedicalRecordStatus.Incomplete || m.Status == MedicalRecordStatus.Deficient));
        }

        if (retentionDue == true)
        {
            charts = charts.Where(m => m.Status == MedicalRecordStatus.Archived && m.RetainUntil != null && m.RetainUntil < today);
        }

        if (assignedPractitionerId is not null)
        {
            charts = charts.Where(m => m.AssignedPractitionerId == assignedPractitionerId);
        }

        if (page.SearchTerm is not null)
        {
            // A patient, or a paper file by the number on it.
            var matching = db.Set<Patient>().Search(page.SearchTerm, protector).Select(p => p.Id);
            var fileNumber = page.SearchTerm.Trim().ToUpperInvariant();
            charts = charts.Where(m => matching.Contains(m.PatientId) || m.PaperFileNumber == fileNumber);
        }

        var result = await (
                from m in charts
                join p in db.Set<Patient>() on m.PatientId equals p.Id
                orderby m.DueDate, m.CreatedAt descending
                select new
                {
                    Chart = m,
                    p.Uhid,
                    p.FullName,
                    Open = m.Deficiencies.Count(d => d.ResolvedAt == null),
                })
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(x => new MedicalRecordDto(x.Chart.Id, x.Chart.PatientId, x.Uhid, x.FullName, x.Chart.EncounterId, x.Chart.AdmissionId,
            x.Chart.VisitId, x.Chart.AdmittedAt, x.Chart.DischargedAt, x.Chart.Status, x.Chart.AssignedPractitionerId, x.Chart.DueDate,
            x.Chart.DueDate is { } due && due < today && x.Chart.Status is MedicalRecordStatus.Incomplete or MedicalRecordStatus.Deficient, x.Chart.IsMlc,
            x.Chart.CompletedAt, x.Chart.ArchivedAt, x.Chart.ArchiveLocation, x.Chart.RetainUntil, x.Open, [], x.Chart.Version, MedicalRecordService.Paper(x.Chart))));
    }

    private static async Task<IResult> Create(CreateMedicalRecordRequest request, MedicalRecordService charts, CancellationToken ct) =>
        (await charts.CreateAsync(request, ct)).ToCreated(m => $"/api/v1/mrd/records/{m.Id}");

    private static async Task<IResult> Get(Guid id, MedicalRecordService charts, ClinicalAccess access, CancellationToken ct)
    {
        var chart = await charts.Charts().AsNoTracking().Include(m => m.Deficiencies).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (chart is null)
        {
            return Error.NotFound("Medical record", id).ToProblem();
        }

        var opened = await access.OpenAsync(chart.PatientId, "Viewed MRD chart", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await charts.ToDtoAsync(chart, ct));
    }

    private static async Task<IResult> AddDeficiency(Guid id, AddDeficiencyRequest request, MedicalRecordService charts, CancellationToken ct) =>
        (await charts.AddDeficiencyAsync(id, request, ct)).ToOk();

    private static async Task<IResult> ResolveDeficiency(Guid deficiencyId, ResolveDeficiencyRequest request, MedicalRecordService charts, CancellationToken ct) =>
        (await charts.ResolveDeficiencyAsync(deficiencyId, request.Note, ct)).ToOk();

    private static async Task<IResult> Scan(Guid id, MedicalRecordService charts, CancellationToken ct) => (await charts.ScanAsync(id, ct)).ToOk();

    private static async Task<IResult> Complete(Guid id, MedicalRecordService charts, CancellationToken ct) => (await charts.CompleteAsync(id, ct)).ToOk();

    private static async Task<IResult> Archive(Guid id, ArchiveMedicalRecordRequest request, MedicalRecordService charts, CancellationToken ct) =>
        (await charts.ArchiveAsync(id, request.Location, ct)).ToOk();

    /// <summary>
    /// <c>copyOverdue</c> = copies for the patient or a legal authority still waiting past their 72 hours (MCI Regulations
    /// 2002, reg. 1.3.2), the longest-waiting first.
    /// </summary>
    private static async Task<IResult> ListRequests([AsParameters] PageQuery query, RecordRequestStatus? status, bool? copyOverdue, HospitalDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var today = time.Today();
        var now = time.GetUtcNow();
        var requests = db.Set<RecordRequest>().AsNoTracking();
        requests = status switch
        {
            RecordRequestStatus.Overdue => requests.Where(r => r.Status == RecordRequestStatus.Issued && r.DueBackOn < today),
            RecordRequestStatus.Issued => requests.Where(r => r.Status == RecordRequestStatus.Issued && (r.DueBackOn == null || r.DueBackOn >= today)),
            { } s => requests.Where(r => r.Status == s),
            null => requests,
        };

        if (copyOverdue == true)
        {
            var askedBefore = now - RecordRequest.CopyWithin;
            requests = requests.Where(r => r.Status == RecordRequestStatus.Requested && r.RequestedAt < askedBefore
                && (r.IsPatientRequest || r.Purpose == RecordRequestPurpose.PatientCopy || r.Purpose == RecordRequestPurpose.Legal));
        }

        var rows =
            from r in requests
            join p in db.Set<Patient>() on r.PatientId equals p.Id
            select new { Request = r, p.Uhid, p.FullName };
        rows = copyOverdue == true ? rows.OrderBy(x => x.Request.RequestedAt) : rows.OrderByDescending(x => x.Request.RequestedAt);
        var page = await rows.ToPagedResultAsync(query.ToRequest(), ct);
        var ids = page.Items.Select(x => x.Request.Id).ToList();
        var files = (await db.Set<RecordCopyFile>().AsNoTracking().Where(f => ids.Contains(f.RecordRequestId)).ToListAsync(ct)).ToLookup(f => f.RecordRequestId);
        return TypedResults.Ok(page.Map(x => MedicalRecordService.ToDto(x.Request, x.Uhid, x.FullName, today, now, files[x.Request.Id])));
    }

    private static async Task<IResult> CreateRequest(CreateRecordRequestRequest request, MedicalRecordService charts, CancellationToken ct) =>
        (await charts.CreateRequestAsync(request, isPatientRequest: false, facilityId: null, ct)).ToCreated(r => $"/api/v1/mrd/record-requests/{r.Id}");

    /// <summary>The patient's documents a copy can hold (signed, released), newest first.</summary>
    private static async Task<IResult> Documents(Guid id, RecordCopyService copies, CancellationToken ct) => (await copies.DocumentsAsync(id, ct)).ToOk();

    /// <summary>Issues the chart, or a copy; a copy with documents is made now, or not at all when a PDF can't be made (503).</summary>
    private static async Task<IResult> Issue(Guid id, IssueRecordRequest request, RecordCopyService copies, CancellationToken ct)
    {
        try
        {
            return (await copies.IssueAsync(id, request, ct)).ToOk();
        }
        catch (PdfRenderException ex)
        {
            return DocumentResults.PdfUnavailable(ex.Message);
        }
    }

    /// <summary>A file of a copy, byte for byte as it was issued.</summary>
    private static async Task<IResult> CopyFile(Guid id, Guid fileId, RecordCopyService copies, CancellationToken ct)
    {
        var file = await copies.FileAsync(id, fileId, ct);
        return file.IsFailure ? file.Error!.ToProblem() : TypedResults.File(file.Value.Content, "application/pdf", file.Value.File.FileName);
    }

    /// <summary>The certificate under BSA 2023 s.63(4) for a copy, to print and sign.</summary>
    private static async Task<IResult> Certificate(Guid id, RecordCopyService copies, CancellationToken ct)
    {
        var certificate = await copies.CertificateAsync(id, ct);
        return certificate.IsFailure ? certificate.Error!.ToProblem() : TypedResults.Ok(certificate.Value.ToDto());
    }

    private static async Task<IResult> CertificatePdf(Guid id, RecordCopyService copies, IPdfRenderer renderer, CancellationToken ct)
    {
        var certificate = await copies.CertificateAsync(id, ct);
        return certificate.IsFailure ? certificate.Error!.ToProblem() : await certificate.Value.ToPdfAsync(renderer, ct);
    }

    /// <summary>Which copy issued a file with this SHA-256, if any (the staff app works the value out from the file).</summary>
    private static async Task<IResult> Verify(VerifyRecordCopyRequest request, RecordCopyService copies, CancellationToken ct) =>
        (await copies.VerifyAsync(request.Sha256, ct)).ToOk();

    private static async Task<IResult> Return(Guid id, MedicalRecordService charts, CancellationToken ct) => (await charts.ReturnAsync(id, ct)).ToOk();

    private static async Task<IResult> CancelRequest(Guid id, CancelRecordRequest request, MedicalRecordService charts, CancellationToken ct) =>
        (await charts.CancelRequestAsync(id, request.Reason, ct)).ToOk();
}
