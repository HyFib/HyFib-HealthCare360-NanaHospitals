using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Pre-analytical workflow (LIS-02, LIS-03): collection with barcode labels, receipt in the lab by scanning, and
/// rejection with a standard reason followed by a recollection tube. Out-patient collection is gated on payment
/// (setting <c>lab.requirePaymentBeforeCollection</c>, default on); in-patient, emergency and day-care work is never held up.
/// </summary>
internal sealed class SampleService(
    HospitalDbContext db,
    INumberSeries numbers,
    IBillingStatusProvider billing,
    ISettingsService settings,
    DiagnosticsLookups lookups,
    LabWorkflow workflow,
    DiagnosticsRealtime realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string RequirePaymentSetting = "lab.requirePaymentBeforeCollection";

    public async Task<Result<IReadOnlyList<SampleLabel>>> CollectAsync(Guid orderId, CollectSamplesRequest request, CancellationToken ct)
    {
        var order = await db.Set<LabOrder>().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound(nameof(LabOrder), orderId);
        }

        var tubes = await db.Set<LabSample>().Where(s => s.LabOrderId == orderId && s.Status == SampleStatus.Pending).ToListAsync(ct);
        if (request.SampleIds is { Count: > 0 } requested)
        {
            tubes = tubes.Where(s => requested.Contains(s.Id)).ToList();
        }

        if (tubes.Count == 0)
        {
            return Error.Rule("lab.nothing_to_collect", "No samples on this order are waiting to be collected.");
        }

        var tubeIds = tubes.Select(s => s.Id).ToHashSet();
        var items = order.Items.Where(i => i.IsActive && i.SampleId is { } id && tubeIds.Contains(id)).ToList();
        var payment = await CheckPaymentAsync(order, items, ct);
        if (payment.IsFailure)
        {
            return payment.Error!;
        }

        var now = time.GetUtcNow();
        var collectedAt = request.CollectedAt ?? now;
        if (collectedAt > now + DiagnosticsTime.FutureTolerance)
        {
            return Error.Field("collectedAt", "The collection time cannot be in the future.");
        }

        foreach (var tube in tubes)
        {
            var collected = tube.Collect(currentUser.UserId, collectedAt, request.CollectionSite);
            if (collected.IsFailure)
            {
                return collected.Error!;
            }
        }

        foreach (var item in items)
        {
            item.MarkCollected();
        }

        order.RefreshStatus(now);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(order.FacilityId, "lab.sample.collected", new { OrderId = order.Id, SampleIds = tubeIds }, ct);
        return Result<IReadOnlyList<SampleLabel>>.Ok(await LabelsAsync(order, tubes, ct));
    }

    /// <summary>Label data for reprinting (a torn or smudged label must be replaced with an identical one).</summary>
    public async Task<Result<IReadOnlyList<SampleLabel>>> LabelsAsync(Guid orderId, Guid? sampleId, CancellationToken ct)
    {
        var order = await db.Set<LabOrder>().AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound(nameof(LabOrder), orderId);
        }

        var tubes = await db.Set<LabSample>().AsNoTracking()
            .Where(s => s.LabOrderId == orderId && s.Status != SampleStatus.Cancelled && (sampleId == null || s.Id == sampleId))
            .OrderBy(s => s.Section)
            .ToListAsync(ct);
        return Result<IReadOnlyList<SampleLabel>>.Ok(await LabelsAsync(order, tubes, ct));
    }

    /// <summary>Bulk receipt by barcode scan. Each barcode gets its own outcome, so one bad label does not block the rack.</summary>
    public async Task<IReadOnlyList<ReceiveOutcome>> ReceiveAsync(IReadOnlyList<string> barcodes, CancellationToken ct)
    {
        var codes = barcodes.Select(b => b.Trim()).Where(b => b.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var tubes = await db.Set<LabSample>().Where(s => codes.Contains(s.Barcode)).ToListAsync(ct);
        var now = time.GetUtcNow();
        var outcomes = new List<ReceiveOutcome>();
        var received = new List<LabSample>();

        foreach (var code in codes)
        {
            var tube = tubes.FirstOrDefault(s => s.Barcode == code);
            if (tube is null)
            {
                outcomes.Add(new ReceiveOutcome(code, false, null, "No sample has this barcode."));
                continue;
            }

            var result = tube.Receive(currentUser.UserId, now);
            outcomes.Add(result.IsSuccess
                ? new ReceiveOutcome(code, true, tube.Id, null)
                : new ReceiveOutcome(code, false, tube.Id, result.Error!.Message));
            if (result.IsSuccess)
            {
                received.Add(tube);
            }
        }

        if (received.Count > 0)
        {
            await workflow.PrepareResultsAsync(received, ct);
            await workflow.RefreshAsync(received.Select(s => s.LabOrderId).ToList(), now, ct);
            await db.SaveChangesAsync(ct);
            foreach (var facility in received.GroupBy(s => s.FacilityId))
            {
                await realtime.PublishAsync(facility.Key, "lab.sample.received", new { SampleIds = facility.Select(s => s.Id).ToList() }, ct);
            }
        }

        return outcomes;
    }

    public async Task<Result<SampleRejected>> RejectAsync(Guid sampleId, RejectSampleRequest request, CancellationToken ct)
    {
        var tube = await db.Set<LabSample>().FirstOrDefaultAsync(s => s.Id == sampleId, ct);
        if (tube is null)
        {
            return Error.NotFound(nameof(LabSample), sampleId);
        }

        var results = await db.Set<LabResult>().Where(r => r.SampleId == sampleId).ToListAsync(ct);
        if (results.Any(r => r.Status == LabResultStatus.Verified))
        {
            return Error.Rule("lab.sample_has_verified_results", "Some results from this sample are already verified. Reopen them before rejecting the sample.");
        }

        var now = time.GetUtcNow();
        var rejected = tube.Reject(request.Reason, request.Remarks, currentUser.UserId, now);
        if (rejected.IsFailure)
        {
            return rejected.Error!;
        }

        // Values measured on a rejected tube must never reach a report; the audit trail keeps them (soft delete).
        foreach (var result in results)
        {
            db.Remove(result);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var replacement = request.Recollect ? await CreateRecollectionAsync(tube, ct) : null;
        if (replacement is { IsFailure: true })
        {
            return replacement.Error!;
        }

        var items = await db.Set<LabOrderItem>().Where(i => i.SampleId == sampleId && i.Status != LabOrderItemStatus.Cancelled).ToListAsync(ct);
        foreach (var item in items)
        {
            item.AwaitRecollection(replacement?.Value.Id ?? sampleId);
        }

        await workflow.RefreshAsync([tube.LabOrderId], now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.PublishAsync(tube.FacilityId, "lab.sample.rejected", new { SampleId = tube.Id, tube.LabOrderId, RecollectionSampleId = replacement?.Value.Id }, ct);
        return new SampleRejected(tube.Id, replacement?.Value.Id, replacement?.Value.Barcode);
    }

    /// <summary>Requests a recollection for a tube rejected earlier without one.</summary>
    public async Task<Result<SampleRejected>> RecollectAsync(Guid sampleId, CancellationToken ct)
    {
        var tube = await db.Set<LabSample>().FirstOrDefaultAsync(s => s.Id == sampleId, ct);
        if (tube is null)
        {
            return Error.NotFound(nameof(LabSample), sampleId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var replacement = await CreateRecollectionAsync(tube, ct);
        if (replacement.IsFailure)
        {
            return replacement.Error!;
        }

        var items = await db.Set<LabOrderItem>().Where(i => i.SampleId == sampleId && i.Status != LabOrderItemStatus.Cancelled).ToListAsync(ct);
        foreach (var item in items)
        {
            item.AwaitRecollection(replacement.Value.Id);
        }

        await workflow.RefreshAsync([tube.LabOrderId], time.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.PublishAsync(tube.FacilityId, "lab.sample.recollection", new { SampleId = tube.Id, RecollectionSampleId = replacement.Value.Id }, ct);
        return new SampleRejected(tube.Id, replacement.Value.Id, replacement.Value.Barcode);
    }

    public async Task<SampleLookup?> FindByBarcodeAsync(string barcode, CancellationToken ct)
    {
        var code = barcode.Trim();
        var tube = await db.Set<LabSample>().AsNoTracking().FirstOrDefaultAsync(s => s.Barcode == code, ct);
        if (tube is null)
        {
            return null;
        }

        var order = await db.Set<LabOrder>().AsNoTracking().Where(o => o.Id == tube.LabOrderId).Select(o => new { o.AccessionNumber }).FirstAsync(ct);
        var tests = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => i.SampleId == tube.Id && i.Status != LabOrderItemStatus.Cancelled)
            .Select(i => i.TestName)
            .ToListAsync(ct);
        var patient = await lookups.PatientAsync(tube.PatientId, ct);
        return new SampleLookup(
            tube.Id, tube.Barcode, tube.Status, tube.LabOrderId, order.AccessionNumber, tube.PatientId, patient?.Uhid ?? string.Empty,
            patient?.FullName ?? string.Empty, patient?.AgeSex(DiagnosticsTime.Today(time)) ?? string.Empty, tube.Container, tube.SpecimenType,
            tube.Section, tube.Priority, tests, tube.CollectedAt, tube.ReceivedAt, tube.RejectionReason);
    }

    /// <summary>Applies the payment gate for the tests on the tubes being drawn.</summary>
    internal async Task<Result> CheckPaymentAsync(LabOrder order, IReadOnlyCollection<LabOrderItem> items, CancellationToken ct)
    {
        var required = await settings.GetAsync<bool?>(RequirePaymentSetting, order.FacilityId, ct) ?? true;
        if (!required || !order.PatientClass.PaysBeforeService() || items.Count == 0)
        {
            return Result.Success();
        }

        var lines = items.Select(i => new PaymentLine(i.Id.ToString(), i.TestName, i.IsPackageComponent)).ToList();
        var charged = lines.Where(l => !l.CoveredByPackage).Select(l => l.Reference).ToList();
        var states = charged.Count == 0
            ? new Dictionary<string, ChargeBillingState>()
            : await billing.GetStatesAsync(ChargeSources.Lab, charged, ct);
        return PaymentGate.Check(order.PatientClass, required, lines, states);
    }

    private async Task<Result<LabSample>> CreateRecollectionAsync(LabSample tube, CancellationToken ct)
    {
        if (tube.Status != SampleStatus.Rejected)
        {
            return Error.Rule("sample.not_rejected", "Only a rejected sample can be recollected.");
        }

        var barcode = await numbers.NextAsync(LabOrderService.BarcodeSeries, null, LabOrderService.BarcodeFormat, NumberResetPolicy.Never, ct);
        var replacement = tube.CreateRecollection(barcode);
        if (replacement.IsSuccess)
        {
            db.Add(replacement.Value);
        }

        return replacement;
    }

    private async Task<IReadOnlyList<SampleLabel>> LabelsAsync(LabOrder order, IReadOnlyCollection<LabSample> tubes, CancellationToken ct)
    {
        var patient = await lookups.PatientAsync(order.PatientId, ct);
        var testIds = order.Items.Select(i => i.LabTestId).Distinct().ToList();
        var shortNames = await db.Set<LabTest>().AsNoTracking()
            .Where(t => testIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.ShortName ?? t.Code, ct);
        var today = DiagnosticsTime.Today(time);

        return tubes.Select(s => new SampleLabel(
            s.Id,
            s.Barcode,
            order.AccessionNumber,
            patient?.Uhid ?? string.Empty,
            patient?.FullName ?? string.Empty,
            patient?.AgeSex(today) ?? string.Empty,
            s.Container,
            s.SpecimenType,
            s.Section,
            s.Priority,
            order.Items.Where(i => i.SampleId == s.Id && i.IsActive).Select(i => shortNames.GetValueOrDefault(i.LabTestId, i.TestCode)).ToList(),
            s.CollectedAt)).ToList();
    }
}
