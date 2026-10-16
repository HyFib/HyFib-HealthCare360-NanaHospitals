using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Localization;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Portal;

/// <summary>
/// /api/v1/portal/reports: released lab, radiology and cardiology reports for the patient and family members who
/// share the verified mobile number (PP-02, LIS-07), each also as a PDF download. Only released content is returned,
/// access is enforced by <see cref="PortalPatientScope"/>, and every read is written to the PHI access audit.
/// </summary>
internal static class PortalReportEndpoints
{
    private const int MaxItems = 200;

    public static void Map(IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup("/api/v1/portal/reports").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal)
            .RequireFeature(FeatureKeys.PatientPortal);
        portal.MapGet("/", List);
        portal.MapGet("/lab/{id:guid}", Lab);
        portal.MapGet("/lab/{id:guid}/pdf", LabPdf);
        portal.MapGet("/radiology/{id:guid}", Radiology);
        portal.MapGet("/radiology/{id:guid}/pdf", RadiologyPdf);
        portal.MapGet("/cardiology/{id:guid}", Cardiology);
        portal.MapGet("/cardiology/{id:guid}/pdf", CardiologyPdf);
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
        var (view, problem) = await OpenLabAsync(id, n => $"Viewed lab report {n} (patient portal)", builder, scope, audit, ct);
        return problem ?? TypedResults.Ok(view);
    }

    /// <summary>The released report as a PDF: the same document the hospital prints and sends.</summary>
    private static async Task<IResult> LabPdf(
        Guid id, LabReportBuilder builder, ReportDocumentFrame frame, IPdfRenderer renderer, PortalPatientScope scope, IAuditLogger audit, HttpContext http,
        CancellationToken ct)
    {
        var (view, problem) = await OpenLabAsync(id, n => $"Downloaded lab report {n} as a PDF (patient portal)", builder, scope, audit, ct);
        return problem ?? await (await LabReportDocument.BuildAsync(view!, releasedOnly: true, frame, DocumentText.For(http.PatientLanguage()), ct)).ToPdfAsync(renderer, ct);
    }

    private static async Task<(LabReportView? View, IResult? Problem)> OpenLabAsync(
        Guid id, Func<string, string> action, LabReportBuilder builder, PortalPatientScope scope, IAuditLogger audit, CancellationToken ct)
    {
        var view = await builder.BuildAsync(id, releasedOnly: true, ct);
        if (view is null || view.Sections.Count == 0)
        {
            return (null, Error.NotFound("Report", id).ToProblem());
        }

        var access = await scope.EnsureAccessAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return (null, access.Error!.ToProblem());
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, action(view.ReportNumber), ct);
        return (view, null);
    }

    /// <param name="id">The imaging study.</param>
    private static async Task<IResult> Radiology(Guid id, RadiologyReportService reports, PortalPatientScope scope, IAuditLogger audit, CancellationToken ct)
    {
        var (view, problem) = await OpenRadiologyAsync(id, n => $"Viewed radiology report {n} (patient portal)", reports, scope, audit, ct);
        return problem ?? TypedResults.Ok(view);
    }

    /// <summary>The released report as a PDF: the same document the hospital prints and sends.</summary>
    private static async Task<IResult> RadiologyPdf(
        Guid id, RadiologyReportService reports, ReportDocumentFrame frame, IPdfRenderer renderer, PortalPatientScope scope, IAuditLogger audit,
        HttpContext http, CancellationToken ct)
    {
        var (view, problem) = await OpenRadiologyAsync(id, n => $"Downloaded radiology report {n} as a PDF (patient portal)", reports, scope, audit, ct);
        return problem ?? await (await RadiologyReportDocument.BuildAsync(view!, frame, DocumentText.For(http.PatientLanguage()), ct)).ToPdfAsync(renderer, ct);
    }

    private static async Task<(RadiologyReportView? View, IResult? Problem)> OpenRadiologyAsync(
        Guid id, Func<string, string> action, RadiologyReportService reports, PortalPatientScope scope, IAuditLogger audit, CancellationToken ct)
    {
        var view = await reports.ViewAsync(id, releasedOnly: true, ct);
        if (view is null)
        {
            return (null, Error.NotFound("Report", id).ToProblem());
        }

        var access = await scope.EnsureAccessAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return (null, access.Error!.ToProblem());
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, action(view.AccessionNumber), ct);
        return (view, null);
    }

    /// <param name="id">The cardiology study.</param>
    private static async Task<IResult> Cardiology(Guid id, CardiologyService cardiology, PortalPatientScope scope, IAuditLogger audit, CancellationToken ct)
    {
        var (view, problem) = await OpenCardiologyAsync(id, n => $"Viewed cardiology report {n} (patient portal)", cardiology, scope, audit, ct);
        return problem ?? TypedResults.Ok(view);
    }

    /// <summary>The released report as a PDF: the same document the hospital prints and sends.</summary>
    private static async Task<IResult> CardiologyPdf(
        Guid id, CardiologyService cardiology, ReportDocumentFrame frame, IPdfRenderer renderer, PortalPatientScope scope, IAuditLogger audit,
        HttpContext http, CancellationToken ct)
    {
        var (view, problem) = await OpenCardiologyAsync(id, n => $"Downloaded cardiology report {n} as a PDF (patient portal)", cardiology, scope, audit, ct);
        return problem ?? await (await CardiologyReportDocument.BuildAsync(view!, frame, DocumentText.For(http.PatientLanguage()), ct)).ToPdfAsync(renderer, ct);
    }

    private static async Task<(CardiologyReportView? View, IResult? Problem)> OpenCardiologyAsync(
        Guid id, Func<string, string> action, CardiologyService cardiology, PortalPatientScope scope, IAuditLogger audit, CancellationToken ct)
    {
        var view = await cardiology.ViewAsync(id, releasedOnly: true, ct);
        if (view is null)
        {
            return (null, Error.NotFound("Report", id).ToProblem());
        }

        var access = await scope.EnsureAccessAsync(view.Patient.Id, ct);
        if (access.IsFailure)
        {
            return (null, access.Error!.ToProblem());
        }

        await audit.PatientRecordAccessedAsync(view.Patient.Id, action(view.AccessionNumber), ct);
        return (view, null);
    }
}
