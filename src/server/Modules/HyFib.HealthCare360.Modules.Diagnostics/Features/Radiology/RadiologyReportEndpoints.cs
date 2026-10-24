using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;

/// <summary>
/// /api/v1/radiology/studies/{id}/report — draft, submit, sign-off (with mandatory critical-finding call), release,
/// addenda, and the printable report with its PDF (IMG-03).
/// </summary>
internal static class RadiologyReportEndpoints
{
    private static readonly string[] DepartmentPermissions = [DiagnosticsPermissions.RadiologyReportsWrite, DiagnosticsPermissions.RadiologyReportsVerify];

    public static void Map(IEndpointRouteBuilder app)
    {
        string[] readers = [DiagnosticsPermissions.RadiologyRead, DiagnosticsPermissions.RadiologyReportsWrite, DiagnosticsPermissions.RadiologyReportsVerify];

        var report = app.MapGroup("/api/v1/radiology/studies/{id:guid}/report").WithTags("Radiology reports");
        report.MapGet("/", Get).RequireAnyPermission(readers);
        report.MapGet("/print", Print).RequireAnyPermission(readers);
        report.MapGet("/document", Document).RequireAnyPermission(readers);
        report.MapGet("/pdf", Pdf).RequireAnyPermission(readers);
        report.MapPut("/", async (Guid id, SaveRadiologyReportRequest r, RadiologyReportService s, CancellationToken ct) => (await s.SaveDraftAsync(id, r, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyReportsWrite).WithValidation<SaveRadiologyReportRequest>().FirstSaveCreates();
        // Submitting and signing name the version read, so a report changed meanwhile is not signed unseen (REL-04).
        report.MapPost("/submit", async (Guid id, SubmitReportRequest r, RadiologyReportService s, CancellationToken ct) => (await s.SubmitAsync(id, r.Version, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyReportsWrite).WithValidation<SubmitReportRequest>();
        report.MapPost("/verify", async (Guid id, SignReportRequest r, RadiologyReportService s, CancellationToken ct) => (await s.VerifyAsync(id, r, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyReportsVerify).WithValidation<SignReportRequest>();
        report.MapPost("/release", async (Guid id, RadiologyReportService s, CancellationToken ct) => (await s.ReleaseAsync(id, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyReportsVerify);
        report.MapPost("/addenda", async (Guid id, AddAddendumRequest r, RadiologyReportService s, CancellationToken ct) => (await s.AddAddendumAsync(id, r, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyReportsVerify).WithValidation<AddAddendumRequest>();
    }

    /// <summary>The report being written (radiologists) or the released report (clinicians with read-only access).</summary>
    private static async Task<IResult> Get(
        Guid id, RadiologyReportService reports, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user,
        IPermissionService permissions, CancellationToken ct)
    {
        var patientId = await db.Set<ImagingStudy>().AsNoTracking().Where(s => s.Id == id).Select(s => (Guid?)s.PatientId).FirstOrDefaultAsync(ct);
        if (patientId is null)
        {
            return Error.NotFound(nameof(ImagingStudy), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patientId.Value, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var report = await reports.GetAsync(id, ct);
        var releasedOnly = await ReportAccess.ReleasedOnlyAsync(user, permissions, DepartmentPermissions, ct);
        if (report is null || (releasedOnly && report.Status != DiagnosticReportStatus.Released))
        {
            return Error.NotFound("Radiology report", id).ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId.Value, "Viewed radiology report", ct);
        return TypedResults.Ok(report);
    }

    /// <summary>The report's content for printing (letterhead, signatories, QR verification).</summary>
    private static async Task<IResult> Print(
        Guid id, RadiologyReportService reports, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user, IPermissionService permissions,
        CancellationToken ct)
    {
        var (view, problem) = await OpenAsync(id, n => $"Printed radiology report {n}", reports, guard, audit, user, permissions, ct);
        return problem ?? TypedResults.Ok(view);
    }

    /// <summary>The A4 report as the staff app prints it: the same document as the PDF and the patient's copy.</summary>
    private static async Task<IResult> Document(
        Guid id, RadiologyReportService reports, ReportDocumentFrame frame, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user,
        IPermissionService permissions, CancellationToken ct)
    {
        var (view, problem) = await OpenAsync(id, n => $"Printed radiology report {n}", reports, guard, audit, user, permissions, ct);
        return problem ?? TypedResults.Ok((await RadiologyReportDocument.BuildAsync(view!, frame, DocumentText.English, ct)).ToDto());
    }

    private static async Task<IResult> Pdf(
        Guid id, RadiologyReportService reports, ReportDocumentFrame frame, IPdfRenderer renderer, PatientAccessGuard guard, IAuditLogger audit,
        ICurrentUser user, IPermissionService permissions, CancellationToken ct)
    {
        var (view, problem) = await OpenAsync(id, n => $"Downloaded radiology report {n} as a PDF", reports, guard, audit, user, permissions, ct);
        return problem ?? await (await RadiologyReportDocument.BuildAsync(view!, frame, DocumentText.English, ct)).ToPdfAsync(renderer, ct);
    }

    // Every way of opening the printable report applies the same rules: the released report only for read-only users,
    // the restricted record guard, and an entry in the PHI access audit saying what was done with it.
    private static async Task<(RadiologyReportView? View, IResult? Problem)> OpenAsync(
        Guid id, Func<string, string> action, RadiologyReportService reports, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user,
        IPermissionService permissions, CancellationToken ct)
    {
        var releasedOnly = await ReportAccess.ReleasedOnlyAsync(user, permissions, DepartmentPermissions, ct);
        var view = await reports.ViewAsync(id, releasedOnly, ct);
        if (view is null)
        {
            return (null, Error.NotFound("Radiology report", id).ToProblem());
        }

        var access = await guard.EnsureCanOpenAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return (null, access.Error!.ToProblem());
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, action(view.AccessionNumber), ct);
        return (view, null);
    }
}
