using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;

/// <summary>
/// /api/v1/radiology: study lists, scheduling and the technologist workflow (IMG-01), the DICOM modality worklist and
/// PACS reconciliation/viewer (IMG-02), TAT and reject analysis (IMG-04), and AI triage findings (IMG-05).
/// </summary>
internal static class RadiologyEndpoints
{
    /// <summary>Who may open imaging studies (and resolve a report id to its study).</summary>
    internal static readonly string[] StudyReaders =
        [DiagnosticsPermissions.RadiologyRead, DiagnosticsPermissions.RadiologyWorklist, DiagnosticsPermissions.RadiologyReportsWrite];

    public static void Map(IEndpointRouteBuilder app)
    {
        var readers = StudyReaders;

        var studies = app.MapGroup("/api/v1/radiology/studies").WithTags("Radiology");
        studies.MapGet("/", List).RequireAnyPermission(readers);
        studies.MapGet("/{id:guid}", Get).RequireAnyPermission(readers);
        studies.MapPost("/{id:guid}/schedule", async (Guid id, ScheduleStudyRequest r, ImagingService s, CancellationToken ct) => (await s.ScheduleAsync(id, r, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist).WithValidation<ScheduleStudyRequest>();
        studies.MapPost("/{id:guid}/arrive", async (Guid id, ImagingService s, CancellationToken ct) => (await s.ArriveAsync(id, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist);
        studies.MapPost("/{id:guid}/start", async (Guid id, ImagingService s, CancellationToken ct) => (await s.StartAsync(id, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist);
        studies.MapPost("/{id:guid}/acquire", async (Guid id, AcquireStudyRequest r, ImagingService s, CancellationToken ct) => (await s.AcquireAsync(id, r, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist).WithValidation<AcquireStudyRequest>();
        studies.MapPost("/{id:guid}/repeats", async (Guid id, RecordRepeatRequest r, ImagingService s, CancellationToken ct) => (await s.RecordRepeatAsync(id, r, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist).WithValidation<RecordRepeatRequest>();
        studies.MapPost("/{id:guid}/cancel", async (Guid id, CancelStudyRequest r, ImagingService s, CancellationToken ct) => (await s.CancelAsync(id, r.Reason, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist).WithValidation<CancelStudyRequest>();
        studies.MapPost("/{id:guid}/reconcile", async (Guid id, ImagingService s, CancellationToken ct) => (await s.ReconcileAsync(id, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist);
        studies.MapGet("/{id:guid}/viewer", Viewer).RequireAnyPermission(readers);
        studies.MapGet("/{id:guid}/ai-findings", async (Guid id, ImagingService s, CancellationToken ct) => TypedResults.Ok(await s.AiFindingsAsync(id, ct)))
            .RequireAnyPermission(DiagnosticsPermissions.RadiologyReportsWrite, DiagnosticsPermissions.RadiologyReportsVerify);
        studies.MapPost("/{id:guid}/ai-findings", async (Guid id, SubmitAiFindingRequest r, ImagingService s, CancellationToken ct) =>
                (await s.SubmitAiFindingAsync(id, r, ct)).ToCreated(f => $"/api/v1/radiology/studies/{id}/ai-findings"))
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist).WithValidation<SubmitAiFindingRequest>();

        var radiology = app.MapGroup("/api/v1/radiology").WithTags("Radiology");
        radiology.MapGet("/reports/{reportId:guid}", ReportById).RequireAnyPermission(readers);
        radiology.MapGet("/worklist/mwl", async (DateOnly? date, string? station, Modality? modality, ImagingService s, CancellationToken ct) =>
                TypedResults.Ok(await s.WorklistAsync(date, station, modality, ct)))
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist);
        radiology.MapGet("/reporting-worklist", ReportingWorklist)
            .RequireAnyPermission(DiagnosticsPermissions.RadiologyReportsWrite, DiagnosticsPermissions.RadiologyReportsVerify);
        radiology.MapPost("/pacs/reconcile", async (ImagingService s, CancellationToken ct) => (await s.ReconcileRecentAsync(ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyWorklist);
        radiology.MapPost("/ai-findings/{findingId:guid}/review", async (Guid findingId, ReviewAiFindingRequest r, ImagingService s, CancellationToken ct) =>
                (await s.ReviewAiFindingAsync(findingId, r, ct)).ToOk())
            .RequireAnyPermission(DiagnosticsPermissions.RadiologyReportsWrite, DiagnosticsPermissions.RadiologyReportsVerify)
            .WithValidation<ReviewAiFindingRequest>();
        radiology.MapGet("/tat", async (DateOnly? from, DateOnly? to, Modality? modality, ImagingService s, CancellationToken ct) =>
                TypedResults.Ok(await s.TatAsync(from, to, modality, ct)))
            .RequireAnyPermission(DiagnosticsPermissions.RadiologyRead, DiagnosticsPermissions.RadiologyReportsVerify);
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        ImagingStudyStatus? status,
        Modality? modality,
        Guid? patientId,
        HospitalDbContext db,
        DiagnosticsLookups lookups,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var studies = db.Set<ImagingStudy>().AsNoTracking();
        if (patientId is { } pid)
        {
            studies = studies.Where(s => s.PatientId == pid);
        }

        if (patientId is null || from is not null || to is not null)
        {
            var (start, end) = DiagnosticsTime.Range(from, to, time);
            studies = studies.Where(s => (s.OrderedAt >= start && s.OrderedAt < end) || (s.ScheduledAt >= start && s.ScheduledAt < end));
        }

        if (status is not null)
        {
            studies = studies.Where(s => s.Status == status);
        }

        if (modality is not null)
        {
            studies = studies.Where(s => s.Modality == modality);
        }

        if (page.SearchTerm is { } term)
        {
            var accession = term.ToUpperInvariant();
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            studies = studies.Where(s => s.AccessionNumber == accession || matching.Contains(s.PatientId));
        }

        var paged = await studies.OrderByUrgency(s => s.Priority).ThenBy(s => s.ScheduledAt ?? s.OrderedAt).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(await ToListItemsAsync(paged, db, lookups, time, ct));
    }

    /// <summary>Studies waiting for a radiologist: urgent AI triage and STAT first, then the oldest acquisitions.</summary>
    private static async Task<IResult> ReportingWorklist(
        [AsParameters] PageQuery query, Modality? modality, HospitalDbContext db, DiagnosticsLookups lookups, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var studies = db.Set<ImagingStudy>().AsNoTracking()
            .Where(s => s.Status == ImagingStudyStatus.Acquired || s.Status == ImagingStudyStatus.Reported);
        if (modality is not null)
        {
            studies = studies.Where(s => s.Modality == modality);
        }

        var paged = await studies
            .OrderByDescending(s => db.Set<ImagingAiFinding>().Any(f => f.ImagingStudyId == s.Id && f.IsUrgent && f.Status == AiFindingStatus.Preliminary))
            .ThenByUrgency(s => s.Priority)
            .ThenBy(s => s.AcquiredAt)
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(await ToListItemsAsync(paged, db, lookups, time, ct));
    }

    /// <summary>
    /// The study of a radiology report (the doctor's worklist carries report ids; the screens route by study id). Only
    /// ids and the status are returned, so there is no PHI audit here; opening the study audits as usual.
    /// </summary>
    private static async Task<IResult> ReportById(Guid reportId, HospitalDbContext db, CancellationToken ct)
    {
        var found = await ReportStudyLookup.Radiology(db.Set<RadiologyReport>().AsNoTracking(), db.Set<ImagingStudy>().AsNoTracking(), reportId)
            .FirstOrDefaultAsync(ct);
        return found is null ? Error.NotFound("Radiology report", reportId).ToProblem() : TypedResults.Ok(found);
    }

    private static async Task<IResult> Get(Guid id, ImagingService service, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var detail = await service.DetailAsync(id, ct);
        if (detail is null)
        {
            return Error.NotFound(nameof(ImagingStudy), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(detail.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(detail.PatientId, $"Viewed imaging study {detail.AccessionNumber}", ct);
        return TypedResults.Ok(detail);
    }

    /// <summary>Opens the PACS viewer for the study (from the patient record); image access is a PHI read and is audited.</summary>
    private static async Task<IResult> Viewer(Guid id, ImagingService service, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var study = await db.Set<ImagingStudy>().AsNoTracking().Where(s => s.Id == id).Select(s => new { s.PatientId, s.AccessionNumber }).FirstOrDefaultAsync(ct);
        if (study is null)
        {
            return Error.NotFound(nameof(ImagingStudy), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(study.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var link = await service.ViewerAsync(id, ct);
        if (link.IsSuccess)
        {
            await audit.PatientRecordAccessedAsync(study.PatientId, $"Opened images of {study.AccessionNumber}", ct);
        }

        return link.ToOk();
    }

    private static async Task<PagedResult<ImagingStudyListItem>> ToListItemsAsync(
        PagedResult<ImagingStudy> paged, HospitalDbContext db, DiagnosticsLookups lookups, TimeProvider time, CancellationToken ct)
    {
        var ids = paged.Items.Select(s => s.Id).ToList();
        var patients = await lookups.PatientsAsync(paged.Items.Select(s => s.PatientId).Distinct().ToList(), ct);
        var reports = await db.Set<RadiologyReport>().AsNoTracking().Where(r => ids.Contains(r.ImagingStudyId))
            .Select(r => new { r.ImagingStudyId, r.Status, r.IsCriticalFinding }).ToDictionaryAsync(r => r.ImagingStudyId, ct);
        var urgent = await db.Set<ImagingAiFinding>().AsNoTracking()
            .Where(f => ids.Contains(f.ImagingStudyId) && f.IsUrgent && f.Status == AiFindingStatus.Preliminary)
            .Select(f => f.ImagingStudyId).Distinct().ToListAsync(ct);
        var today = DiagnosticsTime.Today(time);

        return paged.Map(s =>
        {
            var patient = patients.GetValueOrDefault(s.PatientId);
            var report = reports.GetValueOrDefault(s.Id);
            return new ImagingStudyListItem(
                s.Id, s.AccessionNumber, s.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown,
                patient?.AgeOn(today), s.PatientClass, s.StudyName, s.Modality, s.BodyPart, s.Priority, s.Status, s.OrderedAt, s.ScheduledAt, s.Room,
                urgent.Contains(s.Id), report?.Status, report?.IsCriticalFinding ?? false);
        });
    }
}
