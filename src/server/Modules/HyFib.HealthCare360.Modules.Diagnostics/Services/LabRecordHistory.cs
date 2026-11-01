using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// The history of a lab order and of its report, for staff who may read lab orders. An order's: the order, each test on
/// it (ordered, cancelled) and each sample taken for it (collected, by whom and when, received, rejected and why,
/// referred out). A report's: its releases, and each result on it (entered, by hand or from an analyser, verified,
/// corrected) and each amendment, with who asked for and who approved it. It opens by the rules the report's own screen
/// does.
/// </summary>
internal sealed class LabRecordHistory(HospitalDbContext db, PatientAccessGuard guard) : IRecordHistorySource
{
    public IReadOnlyCollection<string> EntityTypes { get; } = [nameof(LabOrder), nameof(LabReport)];

    public IReadOnlyCollection<string> ReadPermissions(string entityType) => [DiagnosticsPermissions.LabOrdersRead];

    public Task<Result<HistoryRecord>> OpenAsync(string entityType, Guid id, CancellationToken ct) =>
        entityType == nameof(LabReport) ? OpenReportAsync(id, ct) : OpenOrderAsync(id, ct);

    private async Task<Result<HistoryRecord>> OpenReportAsync(Guid id, CancellationToken ct)
    {
        var report = await db.Set<LabReport>().Where(r => r.Id == id).Select(r => new { r.PatientId, r.LabOrderId, r.ReportNumber }).FirstOrDefaultAsync(ct);
        if (report is null)
        {
            return Error.NotFound(nameof(LabReport), id);
        }

        var open = await guard.EnsureCanOpenAsync(report.PatientId, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        var results = await db.Set<LabResult>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(r => r.LabOrderId == report.LabOrderId)
            .OrderBy(r => r.DisplayOrder)
            .Select(r => new HistoryPart(nameof(LabResult), r.Id, r.TestName))
            .ToListAsync(ct);
        var amendments = await db.Set<LabReportAmendment>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(a => a.LabReportId == id)
            .Select(a => new { a.Id, a.Revision })
            .ToListAsync(ct);
        return new HistoryRecord("lab report " + report.ReportNumber, report.PatientId,
            [.. results, .. amendments.Select(a => new HistoryPart(nameof(LabReportAmendment), a.Id, $"Amendment {a.Revision}"))]);
    }

    private async Task<Result<HistoryRecord>> OpenOrderAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Set<LabOrder>().Where(o => o.Id == id).Select(o => new { o.PatientId, o.AccessionNumber }).FirstOrDefaultAsync(ct);
        if (order is null)
        {
            return Error.NotFound(nameof(LabOrder), id);
        }

        var open = await guard.EnsureCanOpenAsync(order.PatientId, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        var items = await db.Set<LabOrderItem>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(i => i.LabOrderId == id)
            .Select(i => new HistoryPart(nameof(LabOrderItem), i.Id, i.TestName))
            .ToListAsync(ct);
        var samples = await db.Set<LabSample>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(s => s.LabOrderId == id)
            .Select(s => new HistoryPart(nameof(LabSample), s.Id, "Sample " + s.Barcode))
            .ToListAsync(ct);
        return new HistoryRecord("lab order " + order.AccessionNumber, order.PatientId, [.. items, .. samples]);
    }
}
