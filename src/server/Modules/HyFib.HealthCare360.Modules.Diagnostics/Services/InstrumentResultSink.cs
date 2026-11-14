using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Receives analyser results from the lab-equipment gateway (LIS-04, INT-LAB) and answers bi-directional host queries.
/// <list type="bullet">
/// <item>Only registered, active analysers are accepted, and only for tubes of their own facility.</item>
/// <item>When the analyser names the sample's patient (<see cref="InstrumentResultMessage.Patient"/>), that patient must
/// be able to be the tube's (<see cref="PatientIdentity"/>): the analyser sent another registered patient's UHID, or no
/// UHID to go by and a name that cannot be the tube patient's, and every value of the message is kept among the unmatched
/// results (<see cref="UnmatchedResultReason.PatientMismatch"/>) with nothing of the tube changed or received: what it
/// withdraws or could not obtain is not applied to the tube either. A message that names no patient is taken as
/// before.</item>
/// <item>Channels map to analytes by <c>INSTRUMENT:CHANNEL</c> (preferred) or plain <c>CHANNEL</c> codes. Only analytes
/// ordered on the scanned tube are filled. Channels no test on the tube maps are logged when others in the message
/// matched: analysers send whole profiles, of which a tube may need one test. A channel no active test in the catalogue
/// maps at all is listed for the lab to map or ignore (<see cref="AnalyserChannelSightings"/>).</item>
/// <item>Every other result is kept. One that cannot go on an open test (no sample id, a sample unknown or not in the
/// lab, another patient named for it, a tube none of whose tests maps any channel sent, a value the test cannot hold,
/// another value or another reading of it for a verified test or for a test the message gave already) is stored among
/// the unmatched results (<see cref="UnmatchedLabResult"/>) for a lab user to assign or discard, and the message is
/// acknowledged: the analyser neither sends it for ever nor loses it. One the analyser flagged critical alerts whoever
/// assigns them, once (<see cref="CriticalValueNotifier.AlertUnmatchedAfterCommit"/>). The same reading sent again
/// (after a lost acknowledgement) changes nothing. Verified results are never overwritten; a rerun needs the result to
/// be reopened first.</item>
/// <item>A result the analyser withdraws (posted in error, deleted) stops counting: its value on a test not yet verified
/// is cleared, the test pending again with why; a verified value of its own stands, released or not, and the withdrawal
/// is kept among the unmatched results for a person to check it; and its values kept aside for the sample are
/// discarded, so none is put on a test. A result it could not obtain is noted on the test, for the bench to run it
/// again or enter it by hand. Neither is a value to keep aside, and neither changes anything when sent again.</item>
/// <item>Results are interpreted and delta-checked like manual entry, and keep what the analyser said about them: unit,
/// flags, status and comment. They are auto-validated only when in range, QC was accepted today on this analyser, the
/// delta check passed, and the analyser raised no flag, used the test's unit and reported the result as final.
/// Everything else waits for a pathologist, with the reasons stored on the result, and critical values alert the
/// pathologists at once.</item>
/// </list>
/// A collected tube that reaches an analyser before it was scanned at the lab counter is received automatically, and the
/// other bidirectional analysers that run its tests are sent its order (<see cref="AnalyserOrders"/>), once one of its
/// results goes on it.
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
    AnalyserOrders analyserOrders,
    AnalyserChannelSightings sightings,
    TimeProvider time,
    ILogger<InstrumentResultSink> logger) : IInstrumentResultSink
{
    /// <summary>Pushed on <c>Topics.Lab(facility)</c> when results are added to the unmatched results.</summary>
    public const string UnmatchedEvent = "lab.results.unmatched";

    public async Task<Result> ReceiveAsync(InstrumentResultMessage message, CancellationToken cancellationToken)
    {
        var instrument = await FindInstrumentAsync(message.InstrumentCode, cancellationToken);
        if (instrument is null)
        {
            return Error.Rule("lab.instrument_unknown", $"Analyser '{message.InstrumentCode}' is not registered or is inactive.");
        }

        // The readings no test on the tube maps, checked against the whole catalogue once the message is handled.
        var notOnTube = new List<InstrumentResult>();
        var received = await ReceiveAsync(instrument, message, notOnTube, cancellationToken);
        if (received.IsSuccess)
        {
            try
            {
                await sightings.NoteAsync(instrument, notOnTube, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The results are saved: a failure here (the list or its notice) must not make the analyser send them again.
                logger.LogWarning(ex, "Could not note the channels analyser {Instrument} sent that no test maps", instrument.Code);
            }
        }

        return received;
    }

    private async Task<Result> ReceiveAsync(
        LabInstrument instrument, InstrumentResultMessage message, List<InstrumentResult> notOnTube, CancellationToken cancellationToken)
    {
        var barcode = message.SampleBarcode.Trim();
        if (barcode.Length == 0)
        {
            if (message.Results.Any(r => r.Kind == InstrumentResultKind.Withdrawn))
            {
                // Without a sample id, nothing kept tells which results it withdraws: those stay for the lab to decide.
                logger.LogWarning("Analyser {Instrument} withdrew results without a sample id: {Channels}", instrument.Code,
                    string.Join(", ", message.Results.Where(r => r.Kind == InstrumentResultKind.Withdrawn).Select(r => r.ChannelCode)));
            }

            notOnTube.AddRange(Values(message));
            return await KeepAllAsync(instrument, message, tube: null, UnmatchedResultReason.NoSampleId,
                $"{instrument.Code} sent the result without a sample id.", cancellationToken);
        }

        var tube = await db.Set<LabSample>().FirstOrDefaultAsync(s => s.Barcode == barcode, cancellationToken);
        if (tube is null || tube.FacilityId != instrument.FacilityId)
        {
            notOnTube.AddRange(Values(message));
            return await KeepAllAsync(instrument, message, tube: null, UnmatchedResultReason.UnknownSample,
                $"No sample of {instrument.Code}'s facility has the barcode {barcode}.", cancellationToken);
        }

        // The analyser named another patient for the sample: a misread or swapped barcode would put their values on this
        // tube. Nothing of the message goes on it, and a collected tube is not received for it either.
        if (message.Patient is { } sent && await PatientMismatchAsync(instrument, tube, sent, cancellationToken) is { } mismatch)
        {
            notOnTube.AddRange(Values(message));
            logger.LogWarning("Analyser {Instrument} named another patient for sample {Barcode} than the tube's; its results are kept for the lab",
                instrument.Code, barcode);
            if (message.Results.Any(r => r.Kind != InstrumentResultKind.Value))
            {
                // Under another patient, nothing tells that these concern this tube: its tests are left as they are.
                logger.LogWarning("Analyser {Instrument} withdrew or could not obtain results for sample {Barcode} under another patient: {Channels}",
                    instrument.Code, barcode, string.Join(", ", message.Results.Where(r => r.Kind != InstrumentResultKind.Value).Select(r => r.ChannelCode)));
            }

            return await KeepAllAsync(instrument, message, tube, UnmatchedResultReason.PatientMismatch, mismatch, cancellationToken);
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

            // Saved only with the results below: a message that stores nothing on the tube receives nothing and orders nothing.
            await analyserOrders.SamplesReceivedAsync([tube], instrument.Code, cancellationToken);
        }
        else if (tube.Status is not (SampleStatus.Received or SampleStatus.InProcess or SampleStatus.Completed))
        {
            notOnTube.AddRange(Values(message));
            return await KeepAllAsync(instrument, message, tube, UnmatchedResultReason.SampleNotInLab,
                $"Sample {barcode} is {tube.Status.ToString().ToLowerInvariant()}, not in the lab.", cancellationToken);
        }

        await workflow.PrepareResultsAsync([tube], cancellationToken);
        await db.Set<LabResult>().Where(r => r.SampleId == tube.Id).LoadAsync(cancellationToken);
        var onTube = db.Set<LabResult>().Local.Where(r => r.SampleId == tube.Id).ToList();
        var open = onTube.Where(r => r.Status != LabResultStatus.Verified).ToList();
        var context = await evaluator.LoadAsync(onTube, cancellationToken);
        var qcAccepted = await qc.AcceptedTestsAsync(tube.FacilityId, instrument.Code, open.Select(r => r.LabTestId).Distinct().ToList(), cancellationToken);
        var recorded = new List<LabResult>();
        var told = new List<LabResult>();
        var unplaced = new List<Unplaced>();
        var unmapped = new List<InstrumentResult>();
        var unchanged = 0;

        foreach (var reading in message.Results)
        {
            int Score(LabResult result) => context.Test(result.LabTestId)?.ChannelMatchScore(instrument.Code, reading.ChannelCode) ?? 0;
            if (reading.Kind != InstrumentResultKind.Value)
            {
                // Said of the test the channel maps: the one a value would go on (or went on earlier in this message), else a
                // verified one. A channel no test on the tube maps has nothing to withdraw or note.
                if ((Best(onTube.Where(r => r.Status != LabResultStatus.Verified), Score) ?? Best(onTube, Score)) is not { } subject)
                {
                    continue;
                }

                var outcome = Told(subject, reading, instrument.Code, barcode);
                if (outcome.Kept is { } held)
                {
                    unplaced.Add(held);
                }
                else if (outcome.Changed)
                {
                    told.Add(subject);
                    if (subject.Status == LabResultStatus.Pending && !open.Contains(subject))
                    {
                        // Its value withdrawn, the test is open again: a value later in this message goes on it.
                        open.Add(subject);
                    }
                }
                else
                {
                    unchanged++;
                }

                continue;
            }

            var analyser = Reading(reading);
            if (Best(open, Score) is not { } target)
            {
                // Every test on the tube that maps the channel is verified, or has its value from this message already.
                if (Best(onTube, Score) is not { } stored)
                {
                    unmapped.Add(reading);
                }
                else if (Conflict(stored, reading, instrument.Code, analyser, context, barcode) is { } conflict)
                {
                    unplaced.Add(conflict);
                }
                else
                {
                    unchanged++;
                }

                continue;
            }

            var capture = context.Capture(target, reading.Value, remarks: null, organism: null, colonyCount: null, instrument.Code, analyser);
            if (capture.IsFailure)
            {
                unplaced.Add(new Unplaced(reading, UnmatchedResultReason.InvalidValue,
                    $"{target.TestName} on sample {barcode}: {capture.Error!.Message}", target.Id));
                continue;
            }

            open.Remove(target);
            if (target.HoldsReading(capture.Value))
            {
                // The analyser sent the message again (its acknowledgement was lost): the value is on the test already.
                unchanged++;
                continue;
            }

            var resultedAt = reading.ResultedAt is { } at && at <= now ? at : now;
            target.Record(capture.Value, null, resultedAt);

            var test = context.Test(target.LabTestId);
            var decision = AutoValidationPolicy.Evaluate(new AutoValidationInput(
                target.ResultType,
                target.Flag,
                test?.AllowAutoValidation ?? false,
                instrument.AutoValidationEnabled,
                qcAccepted.Contains(target.LabTestId),
                capture.Value.Delta,
                analyser.Flags,
                context.ReadsBothSexes(target))
            {
                InstrumentUnit = analyser.Unit,
                TestUnit = test?.Unit,
                InstrumentStatus = analyser.Status,
            });
            if (decision.Approved)
            {
                target.Verify(null, now, automatic: true);
            }
            else
            {
                target.HoldForReview(decision.Reasons);
            }

            recorded.Add(target);
        }

        notOnTube.AddRange(unmapped);
        if (recorded.Count == 0 && told.Count == 0 && unchanged == 0 && unplaced.Count == 0)
        {
            // None of the tube's tests maps any channel sent: kept for a person, who can tell what the analyser ran.
            unplaced.AddRange(unmapped.Select(r => new Unplaced(r, UnmatchedResultReason.NoMatchingTest,
                $"No test on sample {barcode} maps {instrument.Code}'s channel {r.ChannelCode}.")));
            unmapped.Clear();
        }

        if (recorded.Count == 0 && told.Count == 0)
        {
            // Nothing goes on the tube, so nothing of it changes: a collected tube is neither received nor ordered.
            db.ChangeTracker.Clear();
            return await KeepAsync(instrument, message, tube, unplaced, cancellationToken);
        }

        await workflow.RefreshAsync([tube.LabOrderId], now, cancellationToken);
        var kept = await AddUnmatchedAsync(instrument, message, tube, unplaced, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        if (unmapped.Count > 0)
        {
            logger.LogInformation("Analyser {Instrument} sent channels no test on sample {Barcode} maps: {Channels}",
                instrument.Code, barcode, string.Join(", ", unmapped.Select(r => r.ChannelCode)));
        }

        if (told.Count > 0)
        {
            logger.LogInformation("Analyser {Instrument} withdrew, or could not obtain, the result of {Tests} on sample {Barcode}",
                instrument.Code, string.Join(", ", told.Select(r => r.TestName).Distinct(StringComparer.Ordinal)), barcode);
        }

        var criticals = recorded.Where(r => r.IsCritical).ToList();
        if (criticals.Count > 0)
        {
            await critical.AlertVerifiersAsync(await results.AlertsAsync(criticals, cancellationToken), cancellationToken);
        }

        await realtime.PublishAsync(tube.FacilityId, "lab.results.received", new { tube.LabOrderId, SampleId = tube.Id }, cancellationToken);
        await AnnounceKeptAsync(instrument, barcode, kept, cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// What the lab is told when the analyser named another patient for the sample than the tube's, or null when its
    /// patient can be the tube's or it named no one to compare (<see cref="PatientMismatch"/>).
    /// </summary>
    private async Task<string?> PatientMismatchAsync(LabInstrument instrument, LabSample tube, InstrumentPatient sent, CancellationToken ct)
    {
        var patient = await lookups.PatientAsync(tube.PatientId, ct);
        if (patient is null)
        {
            return null;
        }

        var sentId = await lookups.WhoseIdAsync(sent.Id, patient, ct);
        return PatientMismatch(instrument.Code, tube.Barcode, sent, sentId, patient.Uhid, patient.FullName);
    }

    /// <summary>
    /// What the lab is told of a patient mismatch (<see cref="PatientIdentity"/>): the analyser sent another registered
    /// patient's UHID, or no UHID to go by and a name that cannot be the tube patient's. Null when its patient can be the
    /// tube's, or it named no one to compare. What the analyser sent is shown on one line, cut to 64 characters.
    /// </summary>
    internal static string? PatientMismatch(
        string instrumentCode, string barcode, InstrumentPatient sent, AnalyserPatientId sentId, string uhid, string fullName)
    {
        var sentName = Shown(sent.Name);
        return PatientIdentity.Compare(sentId, sent.Name, fullName) switch
        {
            PatientComparison.OtherId =>
                $"{instrumentCode} sent sample {barcode} for patient {Shown(sent.Id)}{(sentName is null ? string.Empty : $" ({sentName})")}, " +
                $"not the sample's patient {fullName} ({uhid}).",
            PatientComparison.OtherName => $"{instrumentCode} sent sample {barcode} for {sentName}, not the sample's patient {fullName} ({uhid}).",
            _ => null,
        };

        static string? Shown(string? text) =>
            text is null ? null : TextLimits.Clip(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 64);
    }

    /// <summary>A result for a test whose value is settled already: verified, or given earlier in this message.</summary>
    private static Unplaced? Conflict(
        LabResult stored, InstrumentResult reading, string instrumentCode, AnalyserReading analyser, ResultEvaluationContext context, string barcode)
    {
        var capture = context.Capture(stored, reading.Value, remarks: null, organism: null, colonyCount: null, instrumentCode, analyser);
        return capture.IsFailure
            ? new Unplaced(reading, UnmatchedResultReason.InvalidValue, $"{stored.TestName} on sample {barcode}: {capture.Error!.Message}", stored.Id)
            : Settled(stored, capture.Value, reading, instrumentCode, barcode);
    }

    /// <summary>
    /// What to do with a reading for a test whose value is settled already. Only the reading on the test, the same value
    /// from the same analyser with the same unit, flags, status and comment, is the message sent again and changes
    /// nothing (null). Another value, or the same value read otherwise (a correction, a flag raised on a rerun, a
    /// comment), is kept for a person to decide.
    /// </summary>
    internal static Unplaced? Settled(LabResult stored, ResultCapture capture, InstrumentResult reading, string instrumentCode, string barcode)
    {
        if (stored.HoldsReading(capture))
        {
            return null;
        }

        var sameValue = stored.HoldsValue(capture);
        var differences = Differences(stored, capture.Reading);
        if (stored.Status != LabResultStatus.Verified)
        {
            // Given earlier in this message, so by this analyser.
            var again = sameValue ? $", and the same value came again with {differences}" : string.Empty;
            return new Unplaced(reading, UnmatchedResultReason.RepeatedInMessage,
                $"{instrumentCode} sent {stored.TestName} for sample {barcode} twice in one message; the test has {stored.Describe()}{again}.", stored.Id);
        }

        var sameAnalyser = string.Equals(stored.InstrumentCode, instrumentCode, StringComparison.OrdinalIgnoreCase);
        var source = sameAnalyser ? string.Empty : stored.InstrumentCode is null ? ", typed in" : $" from {stored.InstrumentCode}";
        var sent = (sameValue, sameAnalyser) switch
        {
            (false, _) => capture.Reading?.Unit is { } unit ? $"{capture.Value.ValueText} {unit}" : capture.Value.ValueText,
            (true, true) => $"the same value with {differences}",
            (true, false) => "the same value",
        };
        return new Unplaced(reading, UnmatchedResultReason.DiffersFromVerified,
            $"{stored.TestName} on sample {barcode} is verified as {stored.Describe()}{source}; {instrumentCode} sent {sent}.", stored.Id);
    }

    /// <summary>
    /// Acts on what the analyser said of a test instead of a value. A withdrawal clears its own value from a test not
    /// yet verified, the test pending again with why; beside a verified value of its own, which stands, released or not,
    /// it is kept for a person to check (<see cref="UnmatchedResultReason.Withdrawn"/>). A result not obtained is noted on
    /// a test not yet verified. Anything else changes nothing: no value to withdraw (the message sent again), a value
    /// typed in or from another analyser, a verified result, or the note there already.
    /// </summary>
    internal static TestTold Told(LabResult test, InstrumentResult reading, string instrumentCode, string barcode)
    {
        var sent = reading.Status is { } status ? $"status {status}" : "no status";
        if (reading.Kind == InstrumentResultKind.NotObtained)
        {
            return new TestTold(test.Note(test.Status == LabResultStatus.Pending
                ? $"{instrumentCode} could not obtain a result for this test ({sent}): run it again or enter the result by hand."
                : $"{instrumentCode} reported that it could not obtain a result for this test ({sent})."));
        }

        if (test.Status != LabResultStatus.Verified)
        {
            return new TestTold(test.Withdraw(instrumentCode,
                $"{instrumentCode} withdrew its result {test.Describe()} ({sent}): run the test again or enter the result by hand."));
        }

        return string.Equals(test.InstrumentCode, instrumentCode, StringComparison.OrdinalIgnoreCase)
            ? new TestTold(false, new Unplaced(reading, UnmatchedResultReason.Withdrawn,
                $"{test.TestName} on sample {barcode} is verified as {test.Describe()}; {instrumentCode} withdrew it ({sent}). "
                + "Check it, and reopen or amend it if it is wrong.", test.Id))
            : new TestTold(false);
    }

    /// <summary>How the analyser's reading differs from the one on the result: "status C", "flags H, no comment".</summary>
    private static string Differences(LabResult stored, AnalyserReading? reading)
    {
        var parts = new List<string>();
        if (stored.InstrumentUnit != reading?.Unit)
        {
            parts.Add(reading?.Unit is { } unit ? $"unit {unit}" : "no unit");
        }

        if (stored.InstrumentFlags != reading?.Flags)
        {
            parts.Add(reading?.Flags is { } flags ? $"flags {flags}" : "no flags");
        }

        if (stored.InstrumentStatus != reading?.Status)
        {
            parts.Add(reading?.Status is { } status ? $"status {status}" : "no status");
        }

        if (stored.InstrumentComment != reading?.Comment)
        {
            parts.Add(reading?.Comment is { } comment ? $"the comment \"{comment}\"" : "no comment");
        }

        return parts.Count == 0 ? "another reading" : string.Join(", ", parts);
    }

    /// <summary>The result whose test the channel maps best: the analyser's own channel code beats a plain one.</summary>
    private static LabResult? Best(IEnumerable<LabResult> candidates, Func<LabResult, int> score) =>
        candidates
            .Select(r => (Result: r, Score: score(r)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Result)
            .FirstOrDefault();

    /// <summary>What the analyser said besides the value, cut to what the record keeps, and the value as it sent it.</summary>
    private static AnalyserReading Reading(InstrumentResult reading) => new(
        TextLimits.ClipOrNull(reading.Units, 32),
        TextLimits.ClipOrNull(reading.Flags, 64),
        TextLimits.ClipOrNull(reading.Status, 16),
        TextLimits.ClipOrNull(reading.Comment, 500))
    {
        Value = reading.Value,
    };

    private Task<Result> KeepAllAsync(
        LabInstrument instrument, InstrumentResultMessage message, LabSample? tube, UnmatchedResultReason reason, string detail, CancellationToken ct) =>
        KeepAsync(instrument, message, tube, Values(message).Select(r => new Unplaced(r, reason, detail)).ToList(), ct);

    /// <summary>The message's values: what the analyser says of a test instead (withdrawn, not obtained) is no value to keep.</summary>
    private static List<InstrumentResult> Values(InstrumentResultMessage message) =>
        [.. message.Results.Where(r => r.Kind == InstrumentResultKind.Value)];

    /// <summary>Stores results kept aside on their own (none of the message's results went on the tube).</summary>
    private async Task<Result> KeepAsync(
        LabInstrument instrument, InstrumentResultMessage message, LabSample? tube, IReadOnlyList<Unplaced> unplaced, CancellationToken ct)
    {
        var kept = await AddUnmatchedAsync(instrument, message, tube, unplaced, ct);
        if (kept.Added > 0 || kept.Discarded > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        await AnnounceKeptAsync(instrument, message.SampleBarcode.Trim(), kept, ct);
        return Result.Success();
    }

    /// <summary>
    /// Adds results that could not go on a test to the unmatched results, to be saved by the caller, but not one kept
    /// already from the same analyser for the same sample id (see <see cref="NotKeptYet"/>). Those kept for the sample id
    /// whose channel the message withdraws are discarded (see <see cref="DiscardWithdrawn"/>).
    /// </summary>
    private async Task<KeptAside> AddUnmatchedAsync(
        LabInstrument instrument, InstrumentResultMessage message, LabSample? tube, IReadOnlyList<Unplaced> unplaced, CancellationToken ct)
    {
        var barcode = TextLimits.ClipOrNull(message.SampleBarcode, UnmatchedLabResult.SampleBarcodeLength);
        var withdrawals = barcode is null ? [] : message.Results.Where(r => r.Kind == InstrumentResultKind.Withdrawn).ToList();
        if (unplaced.Count == 0 && withdrawals.Count == 0)
        {
            return default;
        }

        var rows = unplaced.Select(item => new UnmatchedLabResult
        {
            FacilityId = instrument.FacilityId,
            InstrumentCode = instrument.Code,
            SampleBarcode = barcode,
            SampleId = tube?.Id,
            PatientId = tube?.PatientId,
            LabResultId = item.LabResultId,
            ChannelCode = ChannelOf(item.Reading),
            Value = TextLimits.Clip(item.Reading.Value.Trim(), ResultInterpreter.MaxTextLength),
            InstrumentUnit = TextLimits.ClipOrNull(item.Reading.Units, 32),
            InstrumentFlags = TextLimits.ClipOrNull(item.Reading.Flags, 64),
            InstrumentStatus = TextLimits.ClipOrNull(item.Reading.Status, 16),
            InstrumentComment = TextLimits.ClipOrNull(item.Reading.Comment, 500),
            ResultedAt = item.Reading.ResultedAt,
            ReceivedAt = message.ReceivedAt,
            Reason = item.Reason,
            Detail = TextLimits.Clip(item.Detail, 500),
            Status = UnmatchedResultStatus.Open,
        }).ToList();

        // Tracked only when some are to be discarded.
        var query = db.Set<UnmatchedLabResult>().Where(u => u.InstrumentCode == instrument.Code && u.SampleBarcode == barcode);
        var keptBefore = barcode is null ? [] : await (withdrawals.Count > 0 ? query : query.AsNoTracking()).ToListAsync(ct);
        var discarded = DiscardWithdrawn(keptBefore, withdrawals, instrument.Code, time.GetUtcNow());
        var added = NotKeptYet(rows, keptBefore);
        db.AddRange(added);

        // A critical value on no test reaches no pathologist: whoever matches results is told, in this transaction.
        critical.AlertUnmatchedAfterCommit(instrument, added);
        return new KeptAside(added.Count, discarded);
    }

    /// <summary>
    /// Discards, with why, the analyser's open results kept aside for a sample id on the channels it now withdraws, so
    /// none is put on a test. A withdrawal kept for a person to check a verified result stays. Returns how many.
    /// </summary>
    internal static int DiscardWithdrawn(
        IEnumerable<UnmatchedLabResult> keptBefore, IReadOnlyCollection<InstrumentResult> withdrawals, string instrumentCode, DateTimeOffset now)
    {
        var discarded = 0;
        foreach (var row in keptBefore.Where(r => r.Status == UnmatchedResultStatus.Open && r.Reason != UnmatchedResultReason.Withdrawn))
        {
            if (withdrawals.FirstOrDefault(w => ChannelOf(w) == row.ChannelCode) is { } withdrawal
                && row.Discard($"{instrumentCode} withdrew this result ({(withdrawal.Status is { } status ? $"status {status}" : "no status")}).", null, now).IsSuccess)
            {
                discarded++;
            }
        }

        return discarded;
    }

    /// <summary>The channel as a result kept aside stores it.</summary>
    private static string ChannelOf(InstrumentResult reading) => TextLimits.Clip(reading.ChannelCode.Trim(), UnmatchedLabResult.ChannelCodeLength);

    /// <summary>
    /// The results to keep of those built for one analyser and sample id: not one kept for them before, nor one built
    /// twice. The same channel, value, unit, flags, status, comment and time, compared as stored (trimmed and cut to the
    /// columns), is a message sent again; a result differing in any of them is another reading, kept too. Without a
    /// sample id two samples' results cannot be told from a message sent again, so each is kept.
    /// </summary>
    internal static IReadOnlyList<UnmatchedLabResult> NotKeptYet(IReadOnlyList<UnmatchedLabResult> built, IEnumerable<UnmatchedLabResult> keptBefore)
    {
        var kept = keptBefore.Select(KeptResult.Of).ToHashSet();
        return [.. built.Where(row => row.SampleBarcode is null || kept.Add(KeptResult.Of(row)))];
    }

    private async Task AnnounceKeptAsync(LabInstrument instrument, string barcode, KeptAside kept, CancellationToken ct)
    {
        if (kept.Added > 0)
        {
            logger.LogInformation("Analyser {Instrument} sent {Count} result(s) for sample {Barcode} that could not go on its tests; kept for the lab to assign",
                instrument.Code, kept.Added, barcode.Length == 0 ? "(none)" : barcode);
        }

        if (kept.Discarded > 0)
        {
            logger.LogInformation("Analyser {Instrument} withdrew {Count} result(s) for sample {Barcode} kept for the lab to assign; discarded",
                instrument.Code, kept.Discarded, barcode);
        }

        if (kept.Added > 0 || kept.Discarded > 0)
        {
            await realtime.PublishAsync(instrument.FacilityId, UnmatchedEvent, new { Count = kept.Added }, ct);
        }
    }

    /// <summary>Host query: which channels the analyser should run for a scanned tube (tests not yet verified).</summary>
    public async Task<InstrumentOrder?> GetOrderForSampleAsync(string instrumentCode, string sampleBarcode, CancellationToken cancellationToken)
    {
        var instrument = await FindInstrumentAsync(instrumentCode, cancellationToken);
        var tube = instrument is null ? null : await FindTubeAsync(instrument, sampleBarcode, cancellationToken);
        if (instrument is null || tube is null || tube.Status is not (SampleStatus.Collected or SampleStatus.Received or SampleStatus.InProcess))
        {
            return null;
        }

        return await BuildOrderAsync(tube, analyte => analyte.ChannelFor(instrument.Code), cancellationToken);
    }

    /// <summary>
    /// Order download: the tests this analyser runs on a tube in the lab (its own channel codes, or plain codes on its
    /// bench; see <see cref="AnalyserRouting"/>), not yet verified.
    /// </summary>
    public async Task<InstrumentOrder?> GetOrderToSendAsync(string instrumentCode, string sampleBarcode, CancellationToken cancellationToken)
    {
        var instrument = await FindInstrumentAsync(instrumentCode, cancellationToken);
        var tube = instrument is null ? null : await FindTubeAsync(instrument, sampleBarcode, cancellationToken);
        if (instrument is null || tube is null || tube.Status is not (SampleStatus.Received or SampleStatus.InProcess))
        {
            return null;
        }

        return await BuildOrderAsync(tube, analyte => AnalyserRouting.Channel(analyte, instrument, tube.Section), cancellationToken);
    }

    /// <summary>Codes are unique across facilities, so the analyser is found whichever facility the caller is in.</summary>
    public async Task<InstrumentProfile?> GetInstrumentAsync(string instrumentCode, CancellationToken cancellationToken)
    {
        var code = instrumentCode.Trim().ToUpperInvariant();
        var found = await db.Set<LabInstrument>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(i => i.Code == code)
            .Select(i => new { i.Code, i.Name, i.FacilityId, i.IsActive, i.IsBidirectional, i.Protocol })
            .FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : new InstrumentProfile(found.Code, found.Name, found.FacilityId, found.IsActive, found.IsBidirectional, found.Protocol.ToString());
    }

    private async Task<LabSample?> FindTubeAsync(LabInstrument instrument, string sampleBarcode, CancellationToken ct)
    {
        var barcode = sampleBarcode.Trim();
        var tube = await db.Set<LabSample>().AsNoTracking().FirstOrDefaultAsync(s => s.Barcode == barcode, ct);
        return tube is not null && tube.FacilityId == instrument.FacilityId ? tube : null;
    }

    /// <summary>The tube's tests not yet verified or released, as the analyser's channel codes, with the patient.</summary>
    private async Task<InstrumentOrder> BuildOrderAsync(LabSample tube, Func<LabTest, string?> channelOf, CancellationToken ct)
    {
        var items = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => i.SampleId == tube.Id && i.Status != LabOrderItemStatus.Cancelled && i.Status != LabOrderItemStatus.Released)
            .Select(i => new { i.Id, i.LabTestId })
            .ToListAsync(ct);
        var itemIds = items.Select(i => i.Id).ToList();
        var verified = await db.Set<LabResult>().AsNoTracking()
            .Where(r => itemIds.Contains(r.LabOrderItemId) && r.Status == LabResultStatus.Verified)
            .Select(r => r.LabTestId)
            .ToListAsync(ct);

        var layout = await workflow.AnalyteLayoutAsync(items.Select(i => i.LabTestId), ct);
        var channels = items
            .SelectMany(i => layout.GetValueOrDefault(i.LabTestId) ?? [])
            .Where(slot => !verified.Contains(slot.Analyte.Id))
            .Select(slot => channelOf(slot.Analyte))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var patient = await lookups.PatientAsync(tube.PatientId, ct);
        return new InstrumentOrder(tube.Barcode, patient?.FullName ?? string.Empty, patient?.SexCode, patient?.DateOfBirth, channels)
        {
            PatientId = patient?.Uhid,
            Priority = tube.Priority,
            CollectedAt = tube.CollectedAt,
            SpecimenType = tube.SpecimenType.ToString(),
        };
    }

    private Task<LabInstrument?> FindInstrumentAsync(string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return db.Set<LabInstrument>().AsNoTracking().FirstOrDefaultAsync(i => i.Code == normalized && i.IsActive, ct);
    }

    /// <summary>A result that could not go on a test, with why, and the test's result it was meant for when one was found.</summary>
    internal sealed record Unplaced(InstrumentResult Reading, UnmatchedResultReason Reason, string Detail, Guid? LabResultId = null);

    /// <summary>
    /// What a withdrawal or a result not obtained did to the test its channel maps: changed it (withdrawn, or noted), or
    /// is kept for a person (<see cref="Kept"/>), or neither.
    /// </summary>
    internal sealed record TestTold(bool Changed, Unplaced? Kept = null);

    /// <summary>Results added to the unmatched results, and those kept before that the message withdrew.</summary>
    private readonly record struct KeptAside(int Added, int Discarded);

    /// <summary>
    /// A kept result as stored, all the analyser said of it, which tells it from a new one for the same analyser and
    /// sample id: a message sent again repeats every part.
    /// </summary>
    private sealed record KeptResult(
        string ChannelCode, string Value, string? Unit, string? Flags, string? Status, string? Comment, DateTimeOffset? ResultedAt)
    {
        public static KeptResult Of(UnmatchedLabResult row) =>
            new(row.ChannelCode, row.Value, row.InstrumentUnit, row.InstrumentFlags, row.InstrumentStatus, row.InstrumentComment, row.ResultedAt);
    }
}
