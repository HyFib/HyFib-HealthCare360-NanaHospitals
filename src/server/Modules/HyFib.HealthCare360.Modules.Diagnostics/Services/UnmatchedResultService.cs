using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.UnmatchedResults;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// The lab's unmatched analyser results (INT-LAB, LIS-04): results an analyser sent that could not go on an open test,
/// kept by <see cref="InstrumentResultSink"/>. A lab user assigns one to the right sample's test, where it is recorded as
/// an entered result with the analyser's reading and waits for a person to verify it (never auto-validated, since a
/// person chose where it goes), or discards it with a reason. Both are audited.
/// </summary>
internal sealed class UnmatchedResultService(
    HospitalDbContext db,
    ResultEvaluator evaluator,
    LabWorkflow workflow,
    LabResultService results,
    CriticalValueNotifier critical,
    DiagnosticsLookups lookups,
    DiagnosticsRealtime realtime,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>Stored on a result assigned by hand, and shown to whoever verifies it.</summary>
    internal const string AssignedReason = "A lab user put this analyser result on the test from the unmatched results, so a person verifies it.";

    /// <summary>The facility's results waiting to be matched (or those assigned or discarded), newest first.</summary>
    public async Task<PagedResult<UnmatchedResultDto>> ListAsync(PageRequest page, UnmatchedResultStatus status, CancellationToken ct)
    {
        var rows = db.Set<UnmatchedLabResult>().AsNoTracking().Where(u => u.Status == status);
        if (page.SearchTerm is { } term)
        {
            var code = term.ToUpperInvariant();
            rows = rows.Where(u => u.SampleBarcode == term || u.InstrumentCode == code || u.ChannelCode == term);
        }

        var paged = await rows.OrderByDescending(u => u.ReceivedAt).ThenBy(u => u.ChannelCode).ToPagedResultAsync(page, ct);
        var patients = await lookups.PatientsAsync(paged.Items.Select(u => u.PatientId).OfType<Guid>().Distinct().ToList(), ct);
        var resultIds = paged.Items.Select(u => u.LabResultId).OfType<Guid>().Distinct().ToList();
        var tests = resultIds.Count == 0
            ? []
            : await db.Set<LabResult>().AsNoTracking().Where(r => resultIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.TestName, ct);
        var users = await lookups.UserNamesAsync(paged.Items.Select(u => u.ResolvedBy), ct);
        return paged.Map(u =>
        {
            var patient = u.PatientId is { } patientId ? patients.GetValueOrDefault(patientId) : null;
            return new UnmatchedResultDto(
                u.Id, u.InstrumentCode, u.SampleBarcode, u.SampleId, u.PatientId, patient?.Uhid, patient?.FullName, u.LabResultId,
                u.LabResultId is { } resultId ? tests.GetValueOrDefault(resultId) : null, u.ChannelCode, u.Value, u.InstrumentUnit,
                u.InstrumentFlags, u.InstrumentStatus, u.InstrumentComment, u.ResultedAt, u.ReceivedAt, u.Reason, u.Detail, u.Status,
                u.AssignedResultId, u.DiscardReason, u.ResolvedBy is { } by ? users.GetValueOrDefault(by) : null, u.ResolvedAt);
        });
    }

    /// <summary>
    /// Puts the analyser's result on a test of a sample in the lab, through the result entry path: interpreted against
    /// the patient's interval, delta-checked, with the analyser's unit, flags and status, and left for a person to verify.
    /// A verified result is not replaced: a pathologist reopens it first.
    /// </summary>
    public async Task<Result> AssignAsync(Guid id, AssignUnmatchedResultRequest request, CancellationToken ct)
    {
        var unmatched = await db.Set<UnmatchedLabResult>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (unmatched is null)
        {
            return Error.NotFound(nameof(UnmatchedLabResult), id);
        }

        var target = await db.Set<LabResult>().FirstOrDefaultAsync(r => r.Id == request.ResultId, ct);
        if (target is null)
        {
            return Error.NotFound(nameof(LabResult), request.ResultId);
        }

        if (target.Status == LabResultStatus.Verified)
        {
            return Error.Rule("lab.result_verified",
                $"{target.TestName} is verified already. A pathologist reopens it first, if the analyser's result is to replace it.");
        }

        var context = await evaluator.LoadAsync([target], ct);
        var editable = context.EnsureEditable(target);
        if (editable.IsFailure)
        {
            return editable;
        }

        var reading = new AnalyserReading(unmatched.InstrumentUnit, unmatched.InstrumentFlags, unmatched.InstrumentStatus, unmatched.InstrumentComment);
        var capture = context.Capture(target, unmatched.Value, remarks: null, organism: null, colonyCount: null, unmatched.InstrumentCode, reading);
        if (capture.IsFailure)
        {
            return Error.Rule("lab.unmatched_value_invalid", $"{unmatched.Value} cannot be the result of {target.TestName}: {capture.Error!.Message}");
        }

        var now = time.GetUtcNow();
        var assigned = unmatched.Assign(target.Id, currentUser.UserId, now);
        if (assigned.IsFailure)
        {
            return assigned;
        }

        var recorded = target.Record(capture.Value, currentUser.UserId, now);
        if (recorded.IsFailure)
        {
            return recorded;
        }

        target.HoldForReview([AssignedReason]);
        var sample = context.Sample(target.SampleId)?.Barcode;
        audit.Add(new AuditEntry(AuditCategories.Clinical, "lab.unmatched.assigned",
            $"Put {unmatched.InstrumentCode}'s {unmatched.ChannelCode} result {unmatched.Value} on {target.TestName} of sample {sample}")
        {
            PatientId = target.PatientId,
            EntityType = nameof(UnmatchedLabResult),
            EntityId = unmatched.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { unmatched.SampleBarcode, sample, resultId = target.Id, reason = unmatched.Reason.ToString() },
        });

        await workflow.RefreshAsync([target.LabOrderId], now, ct);
        await db.SaveChangesAsync(ct);

        if (target.IsCritical)
        {
            await critical.AlertVerifiersAsync(await results.AlertsAsync([target], ct), ct);
        }

        await realtime.PublishAsync(target.FacilityId, "lab.results.entered", new { OrderIds = new[] { target.LabOrderId } }, ct);
        await realtime.PublishAsync(target.FacilityId, InstrumentResultSink.UnmatchedEvent, new { unmatched.Id }, ct);
        return Result.Success();
    }

    /// <summary>Sets the result aside with the reason (a duplicate, a control run, a sample rerun on its own tube).</summary>
    public async Task<Result> DiscardAsync(Guid id, string reason, CancellationToken ct)
    {
        var unmatched = await db.Set<UnmatchedLabResult>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (unmatched is null)
        {
            return Error.NotFound(nameof(UnmatchedLabResult), id);
        }

        var discarded = unmatched.Discard(reason, currentUser.UserId, time.GetUtcNow());
        if (discarded.IsFailure)
        {
            return discarded;
        }

        audit.Add(new AuditEntry(AuditCategories.Clinical, "lab.unmatched.discarded",
            $"Discarded {unmatched.InstrumentCode}'s {unmatched.ChannelCode} result {unmatched.Value}"
            + (unmatched.SampleBarcode is { } barcode ? $" for sample {barcode}" : string.Empty) + $": {unmatched.DiscardReason}")
        {
            PatientId = unmatched.PatientId,
            EntityType = nameof(UnmatchedLabResult),
            EntityId = unmatched.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { unmatched.SampleBarcode, reason = unmatched.Reason.ToString(), discardReason = unmatched.DiscardReason },
        });
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(unmatched.FacilityId, InstrumentResultSink.UnmatchedEvent, new { unmatched.Id }, ct);
        return Result.Success();
    }
}
