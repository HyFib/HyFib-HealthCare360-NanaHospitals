using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;

/// <summary>
/// /api/v1/radiology/studies/{id}/report — draft, submit, sign-off (with mandatory critical-finding call), release,
/// addenda and the printable report (IMG-03).
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
        report.MapPut("/", async (Guid id, SaveRadiologyReportRequest r, RadiologyReportService s, CancellationToken ct) => (await s.SaveDraftAsync(id, r, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyReportsWrite).WithValidation<SaveRadiologyReportRequest>();
        report.MapPost("/submit", async (Guid id, RadiologyReportService s, CancellationToken ct) => (await s.SubmitAsync(id, ct)).ToOk())
            .RequirePermission(DiagnosticsPermissions.RadiologyReportsWrite);
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

    private static async Task<IResult> Print(
        Guid id, RadiologyReportService reports, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user, IPermissionService permissions,
        CancellationToken ct)
    {
        var releasedOnly = await ReportAccess.ReleasedOnlyAsync(user, permissions, DepartmentPermissions, ct);
        var view = await reports.ViewAsync(id, releasedOnly, ct);
        if (view is null)
        {
            return Error.NotFound("Radiology report", id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, $"Printed radiology report {view.AccessionNumber}", ct);
        return TypedResults.Ok(view);
    }
}
