using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>An analyte line on the result sheet of an ordered test (panel components expanded in report order).</summary>
internal sealed record AnalyteSlot(LabTest Analyte, string? GroupName, int DisplayOrder);

/// <summary>
/// Keeps the derived laboratory state consistent. Result placeholders are created per analyte when tubes reach the
/// lab. Item, tube, accession and report statuses are then always recomputed from the results. Every workflow step
/// (manual entry, analyser results, verification, reopening, cancellation, rejection) ends with
/// <see cref="RefreshAsync"/>, so a status can never drift from the facts it summarises.
/// </summary>
internal sealed class LabWorkflow(HospitalDbContext db)
{
    private const int MaxPanelDepth = LabTest.MaxPanelDepth;

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AnalyteSlot>>> AnalyteLayoutAsync(IEnumerable<Guid> orderedTestIds, CancellationToken ct)
    {
        var roots = orderedTestIds.Distinct().ToList();
        var all = new Dictionary<Guid, LabTest>();
        var toLoad = roots;
        for (var depth = 0; depth <= MaxPanelDepth && toLoad.Count > 0; depth++)
        {
            var batch = toLoad;
            var loaded = await db.Set<LabTest>().AsNoTracking().Include(t => t.Components).Where(t => batch.Contains(t.Id)).ToListAsync(ct);
            foreach (var test in loaded)
            {
                all[test.Id] = test;
            }

            toLoad = loaded.Where(t => t.IsPanel)
                .SelectMany(t => t.Components.Select(c => c.TestId))
                .Where(id => !all.ContainsKey(id))
                .Distinct()
                .ToList();
        }

        return roots.Where(all.ContainsKey).ToDictionary(id => id, id => (IReadOnlyList<AnalyteSlot>)Expand(all[id], all));
    }

    /// <summary>
    /// Flattens an ordered test into analyte slots. A panel without components (a set-up error) is reported as a
    /// single free line, so the order can still be resulted rather than silently producing an empty report.
    /// </summary>
    internal static List<AnalyteSlot> Expand(LabTest test, IReadOnlyDictionary<Guid, LabTest> all)
    {
        var slots = new List<AnalyteSlot>();
        Add(test, null, 0);
        return slots;

        void Add(LabTest current, string? group, int depth)
        {
            if (!current.IsPanel || current.Components.Count == 0 || depth >= MaxPanelDepth)
            {
                if (slots.All(s => s.Analyte.Id != current.Id))
                {
                    slots.Add(new AnalyteSlot(current, group, slots.Count + 1));
                }

                return;
            }

            foreach (var component in current.Components.OrderBy(c => c.DisplayOrder))
            {
                if (all.TryGetValue(component.TestId, out var child))
                {
                    Add(child, component.GroupName ?? (depth == 0 ? null : current.Name), depth + 1);
                }
            }
        }
    }

    /// <summary>Creates one pending result per analyte for tubes that have just reached the lab (idempotent).</summary>
    public async Task PrepareResultsAsync(IReadOnlyCollection<LabSample> samples, CancellationToken ct)
    {
        var sampleIds = samples.Select(s => s.Id).ToList();
        await db.Set<LabOrderItem>().Where(i => i.SampleId != null && sampleIds.Contains(i.SampleId.Value)).LoadAsync(ct);
        var items = db.Set<LabOrderItem>().Local.Where(i => i.SampleId is { } id && sampleIds.Contains(id) && i.IsActive).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var itemIds = items.Select(i => i.Id).ToList();
        await db.Set<LabResult>().Where(r => itemIds.Contains(r.LabOrderItemId)).LoadAsync(ct);
        var existing = db.Set<LabResult>().Local
            .Where(r => itemIds.Contains(r.LabOrderItemId))
            .Select(r => (r.LabOrderItemId, r.LabTestId))
            .ToHashSet();

        var layout = await AnalyteLayoutAsync(items.Select(i => i.LabTestId), ct);
        foreach (var item in items)
        {
            var sample = samples.First(s => s.Id == item.SampleId);
            foreach (var slot in layout.GetValueOrDefault(item.LabTestId) ?? [])
            {
                if (!existing.Add((item.Id, slot.Analyte.Id)))
                {
                    continue;
                }

                db.Add(new LabResult
                {
                    FacilityId = item.FacilityId,
                    LabOrderId = item.LabOrderId,
                    LabOrderItemId = item.Id,
                    LabTestId = slot.Analyte.Id,
                    SampleId = sample.Id,
                    PatientId = sample.PatientId,
                    TestCode = slot.Analyte.Code,
                    TestName = slot.Analyte.Name,
                    GroupName = slot.GroupName,
                    DisplayOrder = slot.DisplayOrder,
                    ResultType = slot.Analyte.ResultType,
                    Unit = slot.Analyte.Unit,
                    Method = slot.Analyte.Method,
                    Status = LabResultStatus.Pending,
                });
            }

            item.MarkInLab();
        }
    }

    /// <summary>Recomputes item, tube, accession and report status for the given accessions (tracked, same unit of work).</summary>
    public async Task RefreshAsync(IReadOnlyCollection<Guid> orderIds, DateTimeOffset now, CancellationToken ct)
    {
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        await db.Set<LabOrder>().Where(o => ids.Contains(o.Id)).Include(o => o.Items).LoadAsync(ct);
        await db.Set<LabResult>().Where(r => ids.Contains(r.LabOrderId)).LoadAsync(ct);
        await db.Set<LabSample>().Where(s => ids.Contains(s.LabOrderId)).LoadAsync(ct);
        await db.Set<LabReport>().Where(r => ids.Contains(r.LabOrderId)).LoadAsync(ct);

        // The Local views include changes and additions made earlier in this unit of work, and exclude removed rows.
        var results = db.Set<LabResult>().Local.Where(r => ids.Contains(r.LabOrderId)).ToList();
        var samples = db.Set<LabSample>().Local.Where(s => ids.Contains(s.LabOrderId)).ToList();
        var reports = db.Set<LabReport>().Local.Where(r => ids.Contains(r.LabOrderId)).ToList();

        foreach (var order in db.Set<LabOrder>().Local.Where(o => ids.Contains(o.Id)).ToList())
        {
            foreach (var item in order.Items)
            {
                item.RefreshFromResults(results.Where(r => r.LabOrderItemId == item.Id).ToList(), now);
            }

            foreach (var sample in samples.Where(s => s.LabOrderId == order.Id))
            {
                RefreshSample(
                    sample,
                    order.Items.Where(i => i.SampleId == sample.Id && i.IsActive).ToList(),
                    results.Where(r => r.SampleId == sample.Id).ToList(),
                    now);
            }

            order.RefreshStatus(now);
            reports.FirstOrDefault(r => r.LabOrderId == order.Id)?.Refresh(order.Items);
        }
    }

    /// <summary>A tube is complete when every active test on it is verified; a reopened result puts it back in process.</summary>
    internal static void RefreshSample(LabSample sample, IReadOnlyCollection<LabOrderItem> items, IReadOnlyCollection<LabResult> results, DateTimeOffset now)
    {
        if (sample.Status is not (SampleStatus.Received or SampleStatus.InProcess or SampleStatus.Completed))
        {
            return;
        }

        if (items.Count > 0 && items.All(i => i.Status is LabOrderItemStatus.Verified or LabOrderItemStatus.Released))
        {
            sample.Complete(now);
            return;
        }

        sample.Reopen();
        if (results.Any(r => r.Status != LabResultStatus.Pending))
        {
            sample.StartProcessing();
        }
    }
}
