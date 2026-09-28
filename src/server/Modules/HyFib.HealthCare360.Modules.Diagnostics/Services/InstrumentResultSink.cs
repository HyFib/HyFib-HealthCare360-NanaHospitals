using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Receives analyser results from the lab-equipment gateway (LIS-04, INT-LAB) and answers bi-directional host queries.
/// <list type="bullet">
/// <item>Only registered, active analysers of the tube's facility are accepted.</item>
/// <item>Channels map to analytes by <c>INSTRUMENT:CHANNEL</c> (preferred) or plain <c>CHANNEL</c> codes. Only analytes
/// ordered on the scanned tube are filled.</item>
/// <item>Verified results are never overwritten; a rerun needs the result to be reopened first.</item>
/// <item>Results are interpreted and delta-checked like manual entry. They are auto-validated only when in range, QC
/// was accepted today, the delta check passed and the analyser raised no flag. Everything else waits for a pathologist,
/// and critical values alert the pathologists at once.</item>
/// </list>
/// A collected tube that reaches an analyser before it was scanned at the lab counter is received automatically.
/// </summary>
internal sealed class InstrumentResultSink(
    HospitalDbContext db,
    ResultEvaluator evaluator,
    LabWorkflow workflow,
    QualityControlService qc,
    LabResultService results,
    CriticalValueNotifier critical,
    DiagnosticsLookups lookups,
    DiagnosticsRealtime realtime,
    TimeProvider time,
    ILogger<InstrumentResultSink> logger) : IInstrumentResultSink
{
    public async Task<Result> ReceiveAsync(InstrumentResultMessage message, CancellationToken cancellationToken)
    {
        var instrument = await FindInstrumentAsync(message.InstrumentCode, cancellationToken);
        if (instrument is null)
        {
            return Error.Rule("lab.instrument_unknown", $"Analyser '{message.InstrumentCode}' is not registered or is inactive.");
        }

        var barcode = message.SampleBarcode.Trim();
        var tube = await db.Set<LabSample>().FirstOrDefaultAsync(s => s.Barcode == barcode, cancellationToken);
        if (tube is null)
        {
            return Error.NotFound(nameof(LabSample), barcode);
        }

        if (tube.FacilityId != instrument.FacilityId)
        {
            return Error.Rule("lab.instrument_facility", $"Sample {barcode} belongs to a different facility than analyser {instrument.Code}.");
        }

        var now = time.GetUtcNow();
        if (tube.Status == SampleStatus.Collected)
        {
            var receivedAt = message.ReceivedAt > now ? now : message.ReceivedAt;
            var received = tube.Receive(null, tube.CollectedAt is { } collected && receivedAt < collected ? collected : receivedAt);
            if (received.IsFailure)
            {
                return received;
            }
        }
        else if (tube.Status is not (SampleStatus.Received or SampleStatus.InProcess or SampleStatus.Completed))
        {
            return Error.Rule("lab.sample_not_in_lab", $"Sample {barcode} is {tube.Status.ToString().ToLowerInvariant()}; the results were not accepted.");
        }

        await workflow.PrepareResultsAsync([tube], cancellationToken);
        await db.Set<LabResult>().Where(r => r.SampleId == tube.Id).LoadAsync(cancellationToken);
        var open = db.Set<LabResult>().Local.Where(r => r.SampleId == tube.Id && r.Status != LabResultStatus.Verified).ToList();
        if (open.Count == 0)
        {
            return Error.Rule("lab.no_open_results", $"Every test on sample {barcode} is already verified; reopen a result to accept a rerun.");
        }

        var context = await evaluator.LoadAsync(open, cancellationToken);
        var qcAccepted = await qc.AcceptedTestsAsync(tube.FacilityId, open.Select(r => r.LabTestId).Distinct().ToList(), cancellationToken);
        var recorded = new List<LabResult>();
        var skipped = new List<string>();

        foreach (var reading in message.Results)
        {
            var target = open
                .Select(r => (Result: r, Score: context.Test(r.LabTestId)?.ChannelMatchScore(instrument.Code, reading.ChannelCode) ?? 0))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Result)
                .FirstOrDefault();
            if (target is null)
            {
                skipped.Add(reading.ChannelCode);
                continue;
            }

            var capture = context.Capture(target, reading.Value, remarks: null, organism: null, colonyCount: null, instrument.Code);
            if (capture.IsFailure)
            {
                skipped.Add($"{reading.ChannelCode} ({capture.Error!.Message})");
                continue;
            }

            var resultedAt = reading.ResultedAt is { } at && at <= now ? at : now;
            target.Record(capture.Value, null, resultedAt);

            var decision = AutoValidationPolicy.Evaluate(new AutoValidationInput(
                target.ResultType,
                target.Flag,
                context.Test(target.LabTestId)?.AllowAutoValidation ?? false,
                instrument.AutoValidationEnabled,
                qcAccepted.Contains(target.LabTestId),
                capture.Value.Delta,
                reading.Flags));
            if (decision.Approved)
            {
                target.Verify(null, now, automatic: true);
            }
            else
            {
                logger.LogDebug("Result {ResultId} held for review: {Reasons}", target.Id, string.Join(" ", decision.Reasons));
            }

            recorded.Add(target);
            open.Remove(target);
        }

        if (recorded.Count == 0)
        {
            return Error.Rule("lab.no_results_matched", $"No result in the message matched a test ordered on sample {barcode}.");
        }

        await workflow.RefreshAsync([tube.LabOrderId], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        if (skipped.Count > 0)
        {
            logger.LogInformation("Analyser {Instrument} sent unmapped or invalid channels for sample {Barcode}: {Channels}", instrument.Code, barcode, string.Join(", ", skipped));
        }

        var criticals = recorded.Where(r => r.IsCritical).ToList();
        if (criticals.Count > 0)
        {
            await critical.AlertVerifiersAsync(await results.AlertsAsync(criticals, cancellationToken), cancellationToken);
        }

        await realtime.PublishAsync(tube.FacilityId, "lab.results.received", new { tube.LabOrderId, SampleId = tube.Id }, cancellationToken);
        return Result.Success();
    }

    /// <summary>Host query: which channels the analyser should run for a scanned tube (tests not yet verified).</summary>
    public async Task<InstrumentOrder?> GetOrderForSampleAsync(string instrumentCode, string sampleBarcode, CancellationToken cancellationToken)
    {
        var instrument = await FindInstrumentAsync(instrumentCode, cancellationToken);
        if (instrument is null)
        {
            return null;
        }

        var barcode = sampleBarcode.Trim();
        var tube = await db.Set<LabSample>().AsNoTracking().FirstOrDefaultAsync(s => s.Barcode == barcode, cancellationToken);
        if (tube is null || tube.FacilityId != instrument.FacilityId
            || tube.Status is not (SampleStatus.Collected or SampleStatus.Received or SampleStatus.InProcess))
        {
            return null;
        }

        var items = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => i.SampleId == tube.Id && i.Status != LabOrderItemStatus.Cancelled && i.Status != LabOrderItemStatus.Released)
            .Select(i => new { i.Id, i.LabTestId })
            .ToListAsync(cancellationToken);
        var itemIds = items.Select(i => i.Id).ToList();
        var verified = await db.Set<LabResult>().AsNoTracking()
            .Where(r => itemIds.Contains(r.LabOrderItemId) && r.Status == LabResultStatus.Verified)
            .Select(r => r.LabTestId)
            .ToListAsync(cancellationToken);

        var layout = await workflow.AnalyteLayoutAsync(items.Select(i => i.LabTestId), cancellationToken);
        var channels = items
            .SelectMany(i => layout.GetValueOrDefault(i.LabTestId) ?? [])
            .Where(slot => !verified.Contains(slot.Analyte.Id))
            .Select(slot => slot.Analyte.ChannelFor(instrument.Code))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var patient = await lookups.PatientAsync(tube.PatientId, cancellationToken);
        return new InstrumentOrder(
            tube.Barcode,
            patient?.FullName ?? string.Empty,
            patient?.SexCode,
            patient?.DateOfBirth,
            channels);
    }

    private Task<LabInstrument?> FindInstrumentAsync(string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return db.Set<LabInstrument>().AsNoTracking().FirstOrDefaultAsync(i => i.Code == normalized && i.IsActive, ct);
    }
}
