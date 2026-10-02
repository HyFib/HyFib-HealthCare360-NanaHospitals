using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.HealthCheckups;

/// <summary>
/// /api/v1/health-checkups/bookings/{id}/report/document and /pdf: the full health check-up report (MHC-04), with the
/// reviewing doctor's summary and every released result of the package. Bookings belong to the Clinical module, which
/// serves the report's data at <c>/report</c>; the document is made here because it includes the diagnostics results.
/// The rules are those of Clinical's <c>/report</c>: the same permissions, the restricted record guard and an entry in
/// the PHI access audit.
/// </summary>
internal static class HealthCheckupReportEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var bookings = app.MapGroup("/api/v1/health-checkups/bookings").WithTags("Health Checkups");
        bookings.MapGet("/{id:guid}/report/document", Document).RequireAnyPermission(ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrRead);
        bookings.MapGet("/{id:guid}/report/pdf", Pdf).RequireAnyPermission(ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrRead);
    }

    /// <summary>The A4 report as the staff app prints it: the same document as the PDF.</summary>
    private static async Task<IResult> Document(
        Guid id, HealthCheckupService checkups, HealthCheckupResultsBuilder results, ReportDocumentFrame frame, PatientAccessGuard guard,
        IAuditLogger audit, CancellationToken ct)
    {
        var (document, problem) = await BuildAsync(id, n => $"Printed health check-up report {n}", checkups, results, frame, guard, audit, ct);
        return problem ?? TypedResults.Ok(document!.ToDto());
    }

    private static async Task<IResult> Pdf(
        Guid id, HealthCheckupService checkups, HealthCheckupResultsBuilder results, ReportDocumentFrame frame, IPdfRenderer renderer,
        PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var (document, problem) = await BuildAsync(id, n => $"Downloaded health check-up report {n} as a PDF", checkups, results, frame, guard, audit, ct);
        return problem ?? await document!.ToPdfAsync(renderer, ct);
    }

    // The booking is checked and the access recorded before any result is read.
    private static async Task<(PrintableDocument? Document, IResult? Problem)> BuildAsync(
        Guid id, Func<string, string> action, HealthCheckupService checkups, HealthCheckupResultsBuilder results, ReportDocumentFrame frame,
        PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var report = await checkups.ReportAsync(id, ct);
        if (report.IsFailure)
        {
            return (null, report.Error!.ToProblem());
        }

        var checkup = report.Value;
        var access = await guard.EnsureCanOpenAsync(checkup.Booking.PatientId, ct);
        if (access.IsFailure)
        {
            return (null, access.Error!.ToProblem());
        }

        await audit.PatientRecordAccessedAsync(checkup.Booking.PatientId, action(checkup.Booking.BookingNumber), ct);
        var document = await HealthCheckupReportDocument.BuildAsync(checkup, await results.BuildAsync(checkup.Booking.Id, ct), frame, ct);
        return (document, null);
    }
}
