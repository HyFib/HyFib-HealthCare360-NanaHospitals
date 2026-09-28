using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;

/// <summary>/api/v1/lab/reports: report lists, the printable report with QR verification, release and amendment (LIS-02, LIS-07).</summary>
internal static class LabReportEndpoints
{
    private static readonly string[] DepartmentPermissions = [DiagnosticsPermissions.LabResultsEnter, DiagnosticsPermissions.LabResultsVerify];

    public static void Map(IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/api/v1/lab/reports").WithTags("Laboratory");
        reports.MapGet("/", List).RequirePermission(DiagnosticsPermissions.LabOrdersRead);
        reports.MapGet("/{id:guid}", Get).RequirePermission(DiagnosticsPermissions.LabOrdersRead);
        reports.MapPost("/{id:guid}/release", Release).RequirePermission(DiagnosticsPermissions.LabResultsVerify);
        reports.MapPost("/{id:guid}/amend", Amend).RequirePermission(DiagnosticsPermissions.LabResultsVerify).WithValidation<AmendReportRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        LabReportStatus? status,
        DateOnly? from,
        DateOnly? to,
        HospitalDbContext db,
        DiagnosticsLookups lookups,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var (start, end) = DiagnosticsTime.Range(from, to, time);
        var rows =
            from r in db.Set<LabReport>().AsNoTracking()
            join o in db.Set<LabOrder>() on r.LabOrderId equals o.Id
            where o.OrderedAt >= start && o.OrderedAt < end
            select new { r.Id, r.ReportNumber, r.LabOrderId, r.PatientId, r.Status, r.IsPartial, r.Revision, o.OrderedAt, r.ReleasedAt };
        if (status is not null)
        {
            rows = rows.Where(r => r.Status == status);
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            rows = rows.Where(r => r.ReportNumber == number || matching.Contains(r.PatientId));
        }

        var paged = await rows.OrderByDescending(r => r.OrderedAt).ToPagedResultAsync(page, ct);
        var patients = await lookups.PatientsAsync(paged.Items.Select(r => r.PatientId).Distinct().ToList(), ct);
        return TypedResults.Ok(paged.Map(r => new LabReportListItem(
            r.Id, r.ReportNumber, r.LabOrderId, r.PatientId, patients.GetValueOrDefault(r.PatientId)?.Uhid ?? string.Empty,
            patients.GetValueOrDefault(r.PatientId)?.FullName ?? string.Empty, r.Status, r.IsPartial, r.Revision, r.OrderedAt, r.ReleasedAt)));
    }

    /// <summary>The printable report. Clinicians with read-only access see released tests only.</summary>
    private static async Task<IResult> Get(
        Guid id, LabReportBuilder builder, PatientAccessGuard guard, IAuditLogger audit, ICurrentUser user, IPermissionService permissions, CancellationToken ct)
    {
        var releasedOnly = await ReportAccess.ReleasedOnlyAsync(user, permissions, DepartmentPermissions, ct);
        var view = await builder.BuildAsync(id, releasedOnly, ct);
        if (view is null)
        {
            return Error.NotFound(nameof(LabReport), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, $"Viewed lab report {view.ReportNumber}", ct);
        return TypedResults.Ok(view);
    }

    private static async Task<IResult> Release(Guid id, LabReportService reports, CancellationToken ct) =>
        (await reports.ReleaseAsync(id, ct)).ToOk();

    private static async Task<IResult> Amend(Guid id, AmendReportRequest request, LabReportService reports, CancellationToken ct) =>
        (await reports.AmendAsync(id, request, ct)).ToNoContent();
}
