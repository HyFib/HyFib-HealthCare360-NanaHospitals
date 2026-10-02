using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// The diagnostics results of a health check-up booking, for its full report (MHC-04). Package tests and studies carry
/// the booking (<c>PackageBookingId</c>). Each released one is read through its own report's builder, so it prints as in
/// its own report; the others are listed as pending. Only released content is read, whoever asks, and cancelled work is
/// left out.
/// </summary>
internal sealed class HealthCheckupResultsBuilder(
    HospitalDbContext db, LabReportBuilder lab, RadiologyReportService radiology, CardiologyService cardiology)
{
    private const string Laboratory = "Laboratory";

    public async Task<CheckupResults> BuildAsync(Guid bookingId, CancellationToken ct)
    {
        var pending = new List<PendingInvestigation>();

        // Laboratory: the booking's tests sit on one accession per clinical order, so read each report with released
        // tests and keep only the booking's tests on it.
        var labItems = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => i.PackageBookingId == bookingId && i.Status != LabOrderItemStatus.Cancelled)
            .Select(i => new { i.Id, i.LabOrderId, i.TestName, i.Status })
            .ToListAsync(ct);
        var releasedItems = labItems.Where(i => i.Status == LabOrderItemStatus.Released).Select(i => i.Id).ToHashSet();
        var orderIds = labItems.Where(i => releasedItems.Contains(i.Id)).Select(i => i.LabOrderId).Distinct().ToList();
        var reportIds = await db.Set<LabReport>().AsNoTracking()
            .Where(r => orderIds.Contains(r.LabOrderId))
            .OrderBy(r => r.ReportNumber)
            .Select(r => r.Id)
            .ToListAsync(ct);
        var labViews = new List<LabReportView>();
        foreach (var reportId in reportIds)
        {
            if (ForBooking(await lab.BuildAsync(reportId, releasedOnly: true, ct), releasedItems) is { } view)
            {
                labViews.Add(view);
            }
        }

        var printed = labViews.SelectMany(v => v.Sections).SelectMany(s => s.Tests).Select(t => t.OrderItemId).ToHashSet();
        pending.AddRange(labItems
            .Where(i => !printed.Contains(i.Id))
            .OrderBy(i => i.TestName, StringComparer.OrdinalIgnoreCase)
            .Select(i => new PendingInvestigation(
                i.TestName, Laboratory, i.Status == LabOrderItemStatus.Ordered ? PendingInvestigation.NotStarted : PendingInvestigation.Started)));

        var studies = await db.Set<ImagingStudy>().AsNoTracking()
            .Where(s => s.PackageBookingId == bookingId && s.Status != ImagingStudyStatus.Cancelled)
            .OrderBy(s => s.OrderedAt).ThenBy(s => s.AccessionNumber)
            .Select(s => new { s.Id, s.StudyName, s.Status })
            .ToListAsync(ct);
        var radiologyViews = new List<RadiologyReportView>();
        foreach (var study in studies)
        {
            if (await radiology.ViewAsync(study.Id, releasedOnly: true, ct) is { } view)
            {
                radiologyViews.Add(view);
            }
            else
            {
                var started = study.Status is not (ImagingStudyStatus.Ordered or ImagingStudyStatus.Scheduled);
                pending.Add(new PendingInvestigation(study.StudyName, "Radiology", started ? PendingInvestigation.Started : PendingInvestigation.NotStarted));
            }
        }

        var cardiac = await db.Set<CardiologyStudy>().AsNoTracking()
            .Where(s => s.PackageBookingId == bookingId && s.Status != CardiologyStudyStatus.Cancelled)
            .OrderBy(s => s.OrderedAt).ThenBy(s => s.AccessionNumber)
            .Select(s => new { s.Id, s.StudyName, s.Status })
            .ToListAsync(ct);
        var cardiologyViews = new List<CardiologyReportView>();
        foreach (var study in cardiac)
        {
            if (await cardiology.ViewAsync(study.Id, releasedOnly: true, ct) is { } view)
            {
                cardiologyViews.Add(view);
            }
            else
            {
                var started = study.Status is not (CardiologyStudyStatus.Ordered or CardiologyStudyStatus.Scheduled);
                pending.Add(new PendingInvestigation(study.StudyName, "Cardiology", started ? PendingInvestigation.Started : PendingInvestigation.NotStarted));
            }
        }

        return new CheckupResults(labViews, radiologyViews, cardiologyViews, pending);
    }

    /// <summary>The report with only the given tests (the booking's released ones); null when it has none of them.</summary>
    internal static LabReportView? ForBooking(LabReportView? view, IReadOnlySet<Guid> orderItemIds)
    {
        if (view is null)
        {
            return null;
        }

        var sections = view.Sections
            .Select(s => s with { Tests = [.. s.Tests.Where(t => orderItemIds.Contains(t.OrderItemId))] })
            .Where(s => s.Tests.Count > 0)
            .ToList();
        return sections.Count == 0 ? null : view with { Sections = sections };
    }
}
