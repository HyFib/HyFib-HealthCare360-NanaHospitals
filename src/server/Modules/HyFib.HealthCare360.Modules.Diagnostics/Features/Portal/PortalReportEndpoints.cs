using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Portal;

/// <summary>
/// /api/v1/portal/reports: released lab, radiology and cardiology reports for the patient and family members who
/// share the verified mobile number (PP-02, LIS-07). Only released content is returned, access is enforced by
/// <see cref="PortalPatientScope"/>, and every read is written to the PHI access audit.
/// </summary>
internal static class PortalReportEndpoints
{
    private const int MaxItems = 200;

    public static void Map(IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup("/api/v1/portal/reports").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal);
        portal.MapGet("/", List);
        portal.MapGet("/lab/{id:guid}", Lab);
        portal.MapGet("/radiology/{id:guid}", Radiology);
        portal.MapGet("/cardiology/{id:guid}", Cardiology);
    }

    /// <summary>Report list. The item id is the lab report id, or the imaging/cardiology study id.</summary>
    private static async Task<IResult> List(
        Guid? patientId, PortalPatientScope scope, HospitalDbContext db, DiagnosticsLookups lookups, IAuditLogger audit, CancellationToken ct)
    {
        var accessible = await scope.AccessiblePatientIdsAsync(ct);
        if (patientId is { } requested && !accessible.Contains(requested))
        {
            return Error.Forbidden("portal.patient_denied", "This record is not linked to your mobile number.").ToProblem();
        }

        var ids = patientId is { } one ? [one] : accessible.ToList();
        if (ids.Count == 0)
        {
            return TypedResults.Ok(Array.Empty<PortalReportItem>());
        }

        var lab = await (
                from r in db.Set<LabReport>().AsNoTracking()
                where ids.Contains(r.PatientId) && (r.Status == LabReportStatus.Released || r.Status == LabReportStatus.Amended) && r.ReleasedAt != null
                orderby r.ReleasedAt descending
                select new { r.Id, r.LabOrderId, r.PatientId, r.ReportNumber, ReleasedAt = r.ReleasedAt!.Value, r.Status, r.IsPartial })
            .Take(MaxItems)
            .ToListAsync(ct);
        var orderIds = lab.Select(r => r.LabOrderId).ToList();
        var labTests = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => orderIds.Contains(i.LabOrderId) && i.Status == LabOrderItemStatus.Released)
            .Select(i => new { i.LabOrderId, i.TestName })
            .ToListAsync(ct);

        var radiology = await (
                from r in db.Set<RadiologyReport>().AsNoTracking()
                join s in db.Set<ImagingStudy>() on r.ImagingStudyId equals s.Id
                where ids.Contains(r.PatientId) && r.Status == DiagnosticReportStatus.Released && r.ReleasedAt != null
                orderby r.ReleasedAt descending
                select new { StudyId = s.Id, r.PatientId, s.StudyName, s.AccessionNumber, ReleasedAt = r.ReleasedAt!.Value, Addenda = r.Addenda.Count })
            .Take(MaxItems)
            .ToListAsync(ct);

        var cardiology = await (
                from r in db.Set<CardiologyReport>().AsNoTracking()
                join s in db.Set<CardiologyStudy>() on r.CardiologyStudyId equals s.Id
                where ids.Contains(r.PatientId) && r.Status == DiagnosticReportStatus.Released && r.ReleasedAt != null
                orderby r.ReleasedAt descending
                select new { StudyId = s.Id, r.PatientId, s.StudyName, s.AccessionNumber, ReleasedAt = r.ReleasedAt!.Value })
            .Take(MaxItems)
            .ToListAsync(ct);

        var patients = await lookups.PatientsAsync(ids, ct);
        string Name(Guid id) => patients.GetValueOrDefault(id)?.FullName ?? string.Empty;

        var items = lab.Select(r => new PortalReportItem(
                DiagnosticKind.Laboratory, r.Id, r.PatientId, Name(r.PatientId),
                TextLimits.Clip(string.Join(", ", labTests.Where(t => t.LabOrderId == r.LabOrderId).Select(t => t.TestName)), 300),
                r.ReportNumber, r.ReleasedAt, r.Status == LabReportStatus.Amended, r.IsPartial))
            .Concat(radiology.Select(r => new PortalReportItem(
                DiagnosticKind.Radiology, r.StudyId, r.PatientId, Name(r.PatientId), r.StudyName, r.AccessionNumber, r.ReleasedAt, r.Addenda > 0, false)))
            .Concat(cardiology.Select(r => new PortalReportItem(
                DiagnosticKind.Cardiology, r.StudyId, r.PatientId, Name(r.PatientId), r.StudyName, r.AccessionNumber, r.ReleasedAt, false, false)))
            .OrderByDescending(i => i.ReleasedAt)
            .Take(MaxItems)
            .ToList();

        foreach (var patient in items.Select(i => i.PatientId).Distinct())
        {
            await audit.PatientRecordAccessedAsync(patient, "Viewed diagnostic report list (patient portal)", ct);
        }

        return TypedResults.Ok(items);
    }

    private static async Task<IResult> Lab(Guid id, LabReportBuilder builder, PortalPatientScope scope, IAuditLogger audit, CancellationToken ct)
    {
        var view = await builder.BuildAsync(id, releasedOnly: true, ct);
        if (view is null || view.Sections.Count == 0)
        {
            return Error.NotFound("Report", id).ToProblem();
        }

        var access = await scope.EnsureAccessAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, $"Viewed lab report {view.ReportNumber} (patient portal)", ct);
        return TypedResults.Ok(view);
    }

    private static async Task<IResult> Radiology(Guid id, RadiologyReportService reports, PortalPatientScope scope, IAuditLogger audit, CancellationToken ct)
    {
        var view = await reports.ViewAsync(id, releasedOnly: true, ct);
        if (view is null)
        {
            return Error.NotFound("Report", id).ToProblem();
        }

        var access = await scope.EnsureAccessAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, $"Viewed radiology report {view.AccessionNumber} (patient portal)", ct);
        return TypedResults.Ok(view);
    }

    private static async Task<IResult> Cardiology(Guid id, CardiologyService cardiology, PortalPatientScope scope, IAuditLogger audit, CancellationToken ct)
    {
        var view = await cardiology.ViewAsync(id, releasedOnly: true, ct);
        if (view is null)
        {
            return Error.NotFound("Report", id).ToProblem();
        }

        var access = await scope.EnsureAccessAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, $"Viewed cardiology report {view.AccessionNumber} (patient portal)", ct);
        return TypedResults.Ok(view);
    }
}
