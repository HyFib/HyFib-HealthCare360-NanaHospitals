using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Order download (INT-LAB, LIS-04): tells the bidirectional analysers of a sample's facility about the sample when it
/// reaches the lab (scanned at the counter, or received automatically by another analyser's results), when a result is
/// reopened for a rerun, and when tests are cancelled or the sample is rejected. Each call adds one outbox message
/// (<see cref="InstrumentOrderRequest"/>) to the caller's unit of work, so nothing is sent unless the change is saved. The
/// lab gateway keeps the message until the analyser is connected and builds the order when it sends it.
/// Orders go out when a tube reaches the lab, so a tube that never arrived has nothing to cancel.
/// </summary>
internal sealed class AnalyserOrders(HospitalDbContext db, LabWorkflow workflow, IOutbox outbox)
{
    private readonly Dictionary<Guid, IReadOnlyList<LabInstrument>> _analysers = [];

    /// <param name="reportingInstrument">The analyser whose results received the tube: it has the sample already.</param>
    public async Task SamplesReceivedAsync(IReadOnlyCollection<LabSample> tubes, string? reportingInstrument, CancellationToken ct)
    {
        foreach (var facility in tubes.GroupBy(t => t.FacilityId))
        {
            var analysers = await AnalysersAsync(facility.Key, ct);
            if (analysers.Count == 0)
            {
                continue;
            }

            var items = await ItemsOnAsync(facility.Select(t => t.Id).ToList(), ct);
            var layout = await workflow.AnalyteLayoutAsync(items.Select(i => i.LabTestId), ct);
            foreach (var tube in facility)
            {
                var analytes = Analytes(items.Where(i => i.SampleId == tube.Id && i.IsActive), layout);
                Enqueue(tube, InstrumentOrderAction.New, AnalyserRouting.Targets(analysers, tube.Section, analytes, reportingInstrument));
            }
        }
    }

    /// <summary>A verified result was reopened: the analysers that run it are asked to run it again.</summary>
    public async Task ResultReopenedAsync(LabResult result, CancellationToken ct)
    {
        var tube = result.SampleId is { } sampleId ? await TubeAsync(sampleId, ct) : null;
        if (tube is not { ReceivedAt: not null, Status: SampleStatus.Received or SampleStatus.InProcess or SampleStatus.Completed })
        {
            return;
        }

        var analysers = await AnalysersAsync(tube.FacilityId, ct);
        var analyte = analysers.Count == 0 ? null : await db.Set<LabTest>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == result.LabTestId, ct);
        if (analyte is not null)
        {
            Enqueue(tube, InstrumentOrderAction.Rerun, AnalyserRouting.Targets(analysers, tube.Section, [analyte]));
        }
    }

    /// <summary>A test was cancelled after its tube reached the lab: the analysers that were sent it cancel it.</summary>
    public async Task TestCancelledAsync(LabOrderItem item, CancellationToken ct)
    {
        var tube = item.SampleId is { } sampleId ? await TubeAsync(sampleId, ct) : null;
        if (tube?.ReceivedAt is null)
        {
            return;
        }

        var analysers = await AnalysersAsync(tube.FacilityId, ct);
        if (analysers.Count == 0)
        {
            return;
        }

        var layout = await workflow.AnalyteLayoutAsync([item.LabTestId], ct);
        Enqueue(tube, InstrumentOrderAction.Cancel, AnalyserRouting.Targets(analysers, tube.Section, Analytes([item], layout)));
    }

    /// <summary>A received tube was rejected: its tests are cancelled on the analysers (the recollection gets a new barcode).</summary>
    public async Task SampleRejectedAsync(LabSample tube, IReadOnlyCollection<LabOrderItem> items, CancellationToken ct)
    {
        if (tube.ReceivedAt is null)
        {
            return;
        }

        var analysers = await AnalysersAsync(tube.FacilityId, ct);
        if (analysers.Count == 0)
        {
            return;
        }

        var layout = await workflow.AnalyteLayoutAsync(items.Select(i => i.LabTestId), ct);
        Enqueue(tube, InstrumentOrderAction.Cancel, AnalyserRouting.Targets(analysers, tube.Section, Analytes(items.Where(i => i.IsActive), layout)));
    }

    private static List<LabTest> Analytes(IEnumerable<LabOrderItem> items, IReadOnlyDictionary<Guid, IReadOnlyList<AnalyteSlot>> layout) =>
        items.SelectMany(i => layout.GetValueOrDefault(i.LabTestId) ?? [])
            .Select(slot => slot.Analyte)
            .DistinctBy(analyte => analyte.Id)
            .ToList();

    private void Enqueue(LabSample tube, InstrumentOrderAction action, IReadOnlyList<InstrumentOrderTarget> targets)
    {
        if (targets.Count > 0)
        {
            outbox.Enqueue(InstrumentOrderRequest.OutboxType, new InstrumentOrderRequest(tube.Barcode, action, targets), tube.FacilityId);
        }
    }

    /// <summary>The facility's active bidirectional analysers (its own, also when the change runs without a signed-in facility).</summary>
    private async Task<IReadOnlyList<LabInstrument>> AnalysersAsync(Guid facilityId, CancellationToken ct)
    {
        if (!_analysers.TryGetValue(facilityId, out var analysers))
        {
            analysers = await db.Set<LabInstrument>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(i => i.FacilityId == facilityId && i.IsActive && i.IsBidirectional)
                .ToListAsync(ct);
            _analysers[facilityId] = analysers;
        }

        return analysers;
    }

    private async Task<List<LabOrderItem>> ItemsOnAsync(IReadOnlyCollection<Guid> tubeIds, CancellationToken ct)
    {
        // Loaded into the unit of work, so items changed earlier in it are seen as they are now.
        await db.Set<LabOrderItem>().Where(i => i.SampleId != null && tubeIds.Contains(i.SampleId.Value)).LoadAsync(ct);
        return db.Set<LabOrderItem>().Local.Where(i => i.SampleId is { } id && tubeIds.Contains(id)).ToList();
    }

    private async Task<LabSample?> TubeAsync(Guid sampleId, CancellationToken ct) =>
        db.Set<LabSample>().Local.FirstOrDefault(s => s.Id == sampleId)
        ?? await db.Set<LabSample>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(s => s.Id == sampleId, ct);
}
