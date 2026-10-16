using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;

/// <summary>
/// /api/v1/cardiology: ECG, echo, TMT, Holter and ABPM studies (CAR-01). Covers device output upload (CAR-02), the
/// structured report (CAR-03), cardiologist sign-off with release (CAR-04), and the printable report with its PDF.
/// </summary>
internal static class CardiologyEndpoints
{
    private static readonly string[] DepartmentPermissions = [DiagnosticsPermissions.CardiologyStudiesManage, DiagnosticsPermissions.CardiologyReportsVerify];

    /// <summary>Who may open cardiology studies (and resolve a report id to its study).</summary>
    internal static readonly string[] StudyReaders =
        [DiagnosticsPermissions.CardiologyRead, DiagnosticsPermissions.CardiologyStudiesManage, DiagnosticsPermissions.CardiologyReportsVerify];

    public static void Map(IEndpointRouteBuilder app)
    {
        var readers = StudyReaders;
        const string Manage = DiagnosticsPermissions.CardiologyStudiesManage;
        const string Verify = DiagnosticsPermissions.CardiologyReportsVerify;

        var studies = app.MapGroup("/api/v1/cardiology/studies").WithTags("Cardiology");
        studies.MapGet("/", List).RequireAnyPermission(readers);
        studies.MapGet("/{id:guid}", Get).RequireAnyPermission(readers);
        studies.MapPost("/{id:guid}/schedule", async (Guid id, ScheduleCardiologyRequest r, CardiologyService s, CancellationToken ct) => (await s.ScheduleAsync(id, r, ct)).ToOk())
            .RequirePermission(Manage).WithValidation<ScheduleCardiologyRequest>();
        studies.MapPost("/{id:guid}/start", async (Guid id, StartCardiologyRequest r, CardiologyService s, CancellationToken ct) => (await s.StartAsync(id, r, ct)).ToOk())
            .RequirePermission(Manage).WithValidation<StartCardiologyRequest>();
        studies.MapPost("/{id:guid}/files", async (Guid id, AttachDeviceFilesRequest r, CardiologyService s, CancellationToken ct) => (await s.AttachFilesAsync(id, r, ct)).ToOk())
            .RequirePermission(Manage).WithValidation<AttachDeviceFilesRequest>();
        studies.MapPost("/{id:guid}/pacs", SendToPacs).RequirePermission(Manage);
        studies.MapGet("/{id:guid}/viewer", Viewer).RequireAnyPermission(readers);
        studies.MapPost("/{id:guid}/complete", async (Guid id, CompleteCardiologyRequest r, CardiologyService s, CancellationToken ct) => (await s.CompleteAsync(id, r, ct)).ToOk())
            .RequirePermission(Manage).WithValidation<CompleteCardiologyRequest>();
        studies.MapPost("/{id:guid}/cancel", async (Guid id, CancelCardiologyRequest r, CardiologyService s, CancellationToken ct) => (await s.CancelAsync(id, r.Reason, ct)).ToOk())
            .RequirePermission(Manage).WithValidation<CancelCardiologyRequest>();

        studies.MapPut("/{id:guid}/report", async (Guid id, SaveCardiologyReportRequest r, CardiologyService s, CancellationToken ct) => (await s.SaveReportAsync(id, r, ct)).ToOk())
            .RequireAnyPermission(Manage, Verify).WithValidation<SaveCardiologyReportRequest>();
        studies.MapPost("/{id:guid}/report/submit", async (Guid id, CardiologyService s, CancellationToken ct) => (await s.SubmitReportAsync(id, ct)).ToOk())
            .RequireAnyPermission(Manage, Verify);
        studies.MapPost("/{id:guid}/report/verify", async (Guid id, SignCardiologyReportRequest r, CardiologyService s, CancellationToken ct) => (await s.VerifyReportAsync(id, r, ct)).ToOk())
            .RequirePermission(Verify).WithValidation<SignCardiologyReportRequest>();
        studies.MapPost("/{id:guid}/report/release", async (Guid id, CardiologyService s, CancellationToken ct) => (await s.ReleaseReportAsync(id, ct)).ToOk())
            .RequirePermission(Verify);
        studies.MapGet("/{id:guid}/report/print", Print).RequireAnyPermission(readers);
        studies.MapGet("/{id:guid}/report/document", Document).RequireAnyPermission(readers);
        studies.MapGet("/{id:guid}/report/pdf", Pdf).RequireAnyPermission(readers);

        app.MapGet("/api/v1/cardiology/reporting-worklist", ReportingWorklist).WithTags("Cardiology").RequirePermission(Verify);
        app.MapGet("/api/v1/cardiology/reports/{reportId:guid}", ReportById).WithTags("Cardiology").RequireAnyPermission(readers);
    }

    /// <summary>
    /// The study of a cardiology report (the doctor's worklist carries report ids; the screens route by study id). Only
    /// ids and the status are returned, so there is no PHI audit here; opening the study audits as usual.
    /// </summary>
    private static async Task<IResult> ReportById(Guid reportId, HospitalDbContext db, CancellationToken ct)
    {
        var found = await ReportStudyLookup.Cardiology(db.Set<CardiologyReport>().AsNoTracking(), db.Set<CardiologyStudy>().AsNoTracking(), reportId)
            .FirstOrDefaultAsync(ct);
        return found is null ? Error.NotFound("Cardiology report", reportId).ToProblem() : TypedResults.Ok(found);
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        CardiologyStudyStatus? status,
        CardiologyStudyType? studyType,
        Guid? patientId,
        HospitalDbContext db,
        DiagnosticsLookups lookups,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var studies = db.Set<CardiologyStudy>().AsNoTracking();
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

        if (studyType is not null)
        {
            studies = studies.Where(s => s.StudyType == studyType);
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

    /// <summary>Completed studies waiting for the cardiologist: STAT first, then the oldest.</summary>
    private static async Task<IResult> ReportingWorklist(
        [AsParameters] PageQuery query, CardiologyStudyType? studyType, HospitalDbContext db, DiagnosticsLookups lookups, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var studies = db.Set<CardiologyStudy>().AsNoTracking()
            .Where(s => s.Status == CardiologyStudyStatus.Acquired || s.Status == CardiologyStudyStatus.Reported);
        if (studyType is not null)
        {
            studies = studies.Where(s => s.StudyType == studyType);
        }

        var paged = await studies.OrderByUrgency(s => s.Priority).ThenBy(s => s.AcquiredAt).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(await ToListItemsAsync(paged, db, lookups, time, ct));
    }

    /// <summary>Sends the study's DICOM files to the PACS (INT-PACS). Patient data leaves HC360, so a restricted record is checked first.</summary>
    private static async Task<IResult> SendToPacs(Guid id, CardiologyService service, PatientAccessGuard guard, CancellationToken ct)
    {
        if (await service.PatientOfAsync(id, ct) is not { } study)
        {
            return Error.NotFound(nameof(CardiologyStudy), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(study.PatientId, ct);
        return access.IsFailure ? access.Error!.ToProblem() : (await service.SendToPacsAsync(id, ct)).ToOk();
    }

    /// <summary>Opens the study's images in the PACS viewer; opening them is a PHI read and is audited.</summary>
    private static async Task<IResult> Viewer(Guid id, CardiologyService service, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        if (await service.PatientOfAsync(id, ct) is not { } study)
        {
            return Error.NotFound(nameof(CardiologyStudy), id).ToProblem();
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

    private static async Task<IResult> Get(
        Guid id, CardiologyService service, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user, IPermissionService permissions, CancellationToken ct)
    {
        var detail = await service.DetailAsync(id, ct);
        if (detail is null)
        {
            return Error.NotFound(nameof(CardiologyStudy), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(detail.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        // Clinicians with read-only access see the report only once it is released.
        if (detail.Report is { Status: not DiagnosticReportStatus.Released }
            && await ReportAccess.ReleasedOnlyAsync(user, permissions, DepartmentPermissions, ct))
        {
            detail = detail with { Report = null };
        }

        await audit.PatientRecordAccessedAsync(detail.PatientId, $"Viewed cardiology study {detail.AccessionNumber}", ct);
        return TypedResults.Ok(detail);
    }

    /// <summary>The report's content for printing (letterhead, signatories, QR verification).</summary>
    private static async Task<IResult> Print(
        Guid id, CardiologyService service, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user, IPermissionService permissions, CancellationToken ct)
    {
        var (view, problem) = await OpenReportAsync(id, n => $"Printed cardiology report {n}", service, guard, audit, user, permissions, ct);
        return problem ?? TypedResults.Ok(view);
    }

    /// <summary>The A4 report as the staff app prints it: the same document as the PDF and the patient's copy.</summary>
    private static async Task<IResult> Document(
        Guid id, CardiologyService service, ReportDocumentFrame frame, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user,
        IPermissionService permissions, CancellationToken ct)
    {
        var (view, problem) = await OpenReportAsync(id, n => $"Printed cardiology report {n}", service, guard, audit, user, permissions, ct);
        return problem ?? TypedResults.Ok((await CardiologyReportDocument.BuildAsync(view!, frame, DocumentText.English, ct)).ToDto());
    }

    private static async Task<IResult> Pdf(
        Guid id, CardiologyService service, ReportDocumentFrame frame, IPdfRenderer renderer, PatientAccessGuard guard, IAuditLogger audit,
        ICurrentUser user, IPermissionService permissions, CancellationToken ct)
    {
        var (view, problem) = await OpenReportAsync(id, n => $"Downloaded cardiology report {n} as a PDF", service, guard, audit, user, permissions, ct);
        return problem ?? await (await CardiologyReportDocument.BuildAsync(view!, frame, DocumentText.English, ct)).ToPdfAsync(renderer, ct);
    }

    // Every way of opening the printable report applies the same rules: the released report only for read-only users,
    // the restricted record guard, and an entry in the PHI access audit saying what was done with it.
    private static async Task<(CardiologyReportView? View, IResult? Problem)> OpenReportAsync(
        Guid id, Func<string, string> action, CardiologyService service, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user,
        IPermissionService permissions, CancellationToken ct)
    {
        var releasedOnly = await ReportAccess.ReleasedOnlyAsync(user, permissions, DepartmentPermissions, ct);
        var view = await service.ViewAsync(id, releasedOnly, ct);
        if (view is null)
        {
            return (null, Error.NotFound("Cardiology report", id).ToProblem());
        }

        var access = await guard.EnsureCanOpenAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return (null, access.Error!.ToProblem());
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, action(view.AccessionNumber), ct);
        return (view, null);
    }

    private static async Task<PagedResult<CardiologyStudyListItem>> ToListItemsAsync(
        PagedResult<CardiologyStudy> paged, HospitalDbContext db, DiagnosticsLookups lookups, TimeProvider time, CancellationToken ct)
    {
        var ids = paged.Items.Select(s => s.Id).ToList();
        var patients = await lookups.PatientsAsync(paged.Items.Select(s => s.PatientId).Distinct().ToList(), ct);
        var reports = await db.Set<CardiologyReport>().AsNoTracking().Where(r => ids.Contains(r.CardiologyStudyId))
            .Select(r => new { r.CardiologyStudyId, r.Status, r.IsCriticalFinding }).ToDictionaryAsync(r => r.CardiologyStudyId, ct);
        var today = DiagnosticsTime.Today(time);
        return paged.Map(s =>
        {
            var patient = patients.GetValueOrDefault(s.PatientId);
            var report = reports.GetValueOrDefault(s.Id);
            return new CardiologyStudyListItem(
                s.Id, s.AccessionNumber, s.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown,
                patient?.AgeOn(today), s.PatientClass, s.StudyName, s.StudyType, s.Priority, s.Status, s.OrderedAt, s.ScheduledAt, report?.Status,
                report?.IsCriticalFinding ?? false);
        });
    }
}
