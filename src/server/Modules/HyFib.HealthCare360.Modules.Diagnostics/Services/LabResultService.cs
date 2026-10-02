using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Result entry and pathologist verification (LIS-02, LIS-04, LIS-05, LIS-06). Values are interpreted against the
/// patient's reference interval and delta-checked at entry. Verification is refused while QC is failing for the assay
/// (unless overridden with an audited reason). A critical value cannot be verified until the call to the clinician is
/// documented with read-back. Verification sends the critical SMS in the same transaction and in-app alerts after commit.
/// </summary>
internal sealed class LabResultService(
    HospitalDbContext db,
    ResultEvaluator evaluator,
    LabWorkflow workflow,
    QualityControlService qc,
    CriticalValueNotifier critical,
    LabReportService reports,
    DiagnosticsLookups lookups,
    DiagnosticsRealtime realtime,
    AnalyserOrders analyserOrders,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<Result<ResultSheet>> SheetAsync(Guid sampleId, CancellationToken ct)
    {
        var tube = await db.Set<LabSample>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sampleId, ct);
        if (tube is null)
        {
            return Error.NotFound(nameof(LabSample), sampleId);
        }

        var results = await db.Set<LabResult>().AsNoTracking().Include(r => r.Susceptibilities).Where(r => r.SampleId == sampleId).ToListAsync(ct);
        var order = await db.Set<LabOrder>().AsNoTracking().Where(o => o.Id == tube.LabOrderId)
            .Select(o => new { o.AccessionNumber, o.ClinicalNotes }).FirstAsync(ct);
        var patient = await lookups.PatientAsync(tube.PatientId, ct);
        var context = await evaluator.LoadAsync(results, ct);
        var lines = await LinesAsync(results, context, ct);

        return new ResultSheet(
            tube.Id, tube.Barcode, tube.Status, tube.LabOrderId, order.AccessionNumber, tube.PatientId, patient?.Uhid ?? string.Empty,
            patient?.FullName ?? string.Empty, patient?.AgeSex(DiagnosticsTime.Today(time)) ?? string.Empty, order.ClinicalNotes, lines);
    }

    /// <summary>Bulk entry (a whole tube, or a batch from the worklist). All-or-nothing, with per-line error messages.</summary>
    public async Task<Result<IReadOnlyList<ResultSheetLine>>> EnterAsync(EnterResultsRequest request, CancellationToken ct)
    {
        var ids = request.Entries.Select(e => e.ResultId).Distinct().ToList();
        var results = await db.Set<LabResult>().Include(r => r.Susceptibilities).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        if (results.Count != ids.Count)
        {
            return Error.NotFound(nameof(LabResult), string.Join(", ", ids.Except(results.Select(r => r.Id))));
        }

        var context = await evaluator.LoadAsync(results, ct);
        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < request.Entries.Count; i++)
        {
            var entry = request.Entries[i];
            var result = results.First(r => r.Id == entry.ResultId);
            var error = context.EnsureEditable(result).Error
                        ?? Record(result, context, entry, now);
            if (error is not null)
            {
                errors[$"entries[{i}].value"] = [error.Message];
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("lab.results_invalid", "Some results could not be saved. Correct the highlighted values.", errors);
        }

        await workflow.RefreshAsync(results.Select(r => r.LabOrderId).ToList(), now, ct);
        await db.SaveChangesAsync(ct);

        var criticals = results.Where(r => r.IsCritical && r.Status == LabResultStatus.Entered).ToList();
        if (criticals.Count > 0)
        {
            await critical.AlertVerifiersAsync(await AlertsAsync(criticals, ct), ct);
        }

        foreach (var facility in results.GroupBy(r => r.FacilityId))
        {
            await realtime.PublishAsync(facility.Key, "lab.results.entered", new { OrderIds = facility.Select(r => r.LabOrderId).Distinct().ToList() }, ct);
        }

        return Result<IReadOnlyList<ResultSheetLine>>.Ok(await LinesAsync(results, context, ct));
    }

    public async Task<Result<VerificationOutcome>> VerifyAsync(VerifyResultsRequest request, CancellationToken ct)
    {
        var ids = request.ResultIds.Distinct().ToList();
        var results = await db.Set<LabResult>().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        if (results.Count != ids.Count)
        {
            return Error.NotFound(nameof(LabResult), string.Join(", ", ids.Except(results.Select(r => r.Id))));
        }

        var missing = results.Where(r => r.Status == LabResultStatus.Pending).Select(r => r.TestName).ToList();
        if (missing.Count > 0)
        {
            return Error.Rule("lab.result_missing", $"Enter values before verifying: {string.Join(", ", missing)}.");
        }

        var toVerify = results.Where(r => r.Status == LabResultStatus.Entered).ToList();
        var now = time.GetUtcNow();
        var outcome = new VerificationOutcome(0, 0, 0, []);
        if (toVerify.Count == 0 && !request.Release)
        {
            return outcome;
        }

        var context = await evaluator.LoadAsync(toVerify, ct);
        foreach (var result in toVerify)
        {
            var editable = context.EnsureEditable(result);
            if (editable.IsFailure)
            {
                return editable.Error!;
            }
        }

        // QC lockout (LIS-06): refuse, or record an audited override when a reason is given.
        var overrides = 0;
        foreach (var facility in toVerify.GroupBy(r => r.FacilityId))
        {
            var lockouts = await qc.LockoutsAsync(facility.Key, facility.Select(r => r.LabTestId).Distinct().ToList(), ct);
            if (lockouts.Count == 0)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(request.QcOverrideReason))
            {
                var qcFailed = Error.Rule(
                    "lab.qc_lockout",
                    $"Today's QC failed for {string.Join(", ", lockouts.Select(l => l.TestName))}. Resolve QC, or verify with an override reason.");
                return qcFailed with { Data = new { lockouts } };
            }

            overrides += await qc.RecordOverridesAsync(lockouts, request.QcOverrideReason, ct);
        }

        // Critical values (LIS-05): a documented call with read-back is mandatory before verification.
        var criticalResults = toVerify.Where(r => r.IsCritical).ToList();
        var criticalIds = criticalResults.Select(r => r.Id).ToList();
        var alreadyCalled = criticalIds.Count == 0
            ? []
            : await db.Set<CriticalCallLog>().Where(c => c.LabResultId != null && criticalIds.Contains(c.LabResultId.Value))
                .Select(c => c.LabResultId!.Value).Distinct().ToListAsync(ct);
        var needCall = criticalResults.Where(r => !alreadyCalled.Contains(r.Id)).ToList();
        if (needCall.Count > 0)
        {
            if (request.CriticalCall is null)
            {
                var callRequired = Error.Rule(
                    "lab.critical_call_required",
                    $"Critical values need a documented call before verification: {string.Join(", ", needCall.Select(r => $"{r.TestName} {r.Describe()}"))}.");
                return callRequired with { Data = new { results = needCall.Select(r => new { r.Id, r.TestName, value = r.Describe(), r.Flag }).ToList() } };
            }

            foreach (var result in needCall)
            {
                var log = CriticalCallLog.Record(
                    new CriticalFindingRef(DiagnosticKind.Laboratory, result.FacilityId, result.PatientId, $"{result.TestName} {result.Describe()} ({result.Flag})", LabResultId: result.Id),
                    request.CriticalCall,
                    currentUser.UserId,
                    now);
                if (log.IsFailure)
                {
                    return log.Error!;
                }

                db.Add(log.Value);
            }
        }

        foreach (var result in toVerify)
        {
            result.Verify(currentUser.UserId, now, automatic: false);
        }

        var orderIds = results.Select(r => r.LabOrderId).Distinct().ToList();
        await workflow.RefreshAsync(orderIds, now, ct);

        var alerts = await AlertsAsync(criticalResults, ct);
        await critical.QueueDoctorSmsAsync(alerts, ct);

        var released = request.Release ? await reports.ReleaseForOrdersAsync(orderIds, ct) : [];
        await db.SaveChangesAsync(ct);

        await critical.NotifyVerifiedAsync(alerts, ct);
        foreach (var facility in results.GroupBy(r => r.FacilityId))
        {
            await realtime.PublishAsync(facility.Key, "lab.results.verified", new { OrderIds = facility.Select(r => r.LabOrderId).Distinct().ToList() }, ct);
        }

        if (released.Count > 0)
        {
            await reports.AnnounceReleasedAsync(released, ct);
        }

        return new VerificationOutcome(toVerify.Count, needCall.Count, overrides, released.SelectMany(r => r.ReleasedItemIds).ToList());
    }

    /// <summary>Sends a verified, unreleased result back for correction or a rerun (audited).</summary>
    public async Task<Result> ReopenAsync(Guid resultId, string reason, CancellationToken ct)
    {
        var result = await db.Set<LabResult>().FirstOrDefaultAsync(r => r.Id == resultId, ct);
        if (result is null)
        {
            return Error.NotFound(nameof(LabResult), resultId);
        }

        var item = await db.Set<LabOrderItem>().FirstAsync(i => i.Id == result.LabOrderItemId, ct);
        if (item.Status == LabOrderItemStatus.Released)
        {
            return Error.Rule("lab.result_released", $"{result.TestName} is released; amend the report to correct it.");
        }

        var reopened = result.Reopen();
        if (reopened.IsFailure)
        {
            return reopened;
        }

        audit.Add(new AuditEntry(AuditCategories.Clinical, "lab.result.reopened", $"Reopened {result.TestName} ({result.Describe()}): {reason.Trim()}")
        {
            PatientId = result.PatientId,
            EntityType = nameof(LabResult),
            EntityId = result.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });

        var now = time.GetUtcNow();
        await workflow.RefreshAsync([result.LabOrderId], now, ct);

        // Bidirectional analysers that run the test are asked to run it again.
        await analyserOrders.ResultReopenedAsync(result, ct);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(result.FacilityId, "lab.results.reopened", new { result.LabOrderId, ResultId = result.Id }, ct);
        return Result.Success();
    }

    /// <summary>Documents an (additional) call for a critical result, for example a second attempt or a call to the ward.</summary>
    public async Task<Result<Guid>> RecordCriticalCallAsync(Guid resultId, CriticalCallDetails call, CancellationToken ct)
    {
        var result = await db.Set<LabResult>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == resultId, ct);
        if (result is null)
        {
            return Error.NotFound(nameof(LabResult), resultId);
        }

        if (!result.IsCritical)
        {
            return Error.Rule("lab.not_critical", $"{result.TestName} is not flagged critical.");
        }

        var log = CriticalCallLog.Record(
            new CriticalFindingRef(DiagnosticKind.Laboratory, result.FacilityId, result.PatientId, $"{result.TestName} {result.Describe()} ({result.Flag})", LabResultId: result.Id),
            call,
            currentUser.UserId,
            time.GetUtcNow());
        if (log.IsFailure)
        {
            return log.Error!;
        }

        db.Add(log.Value);
        await db.SaveChangesAsync(ct);
        return log.Value.Id;
    }

    internal async Task<IReadOnlyList<CriticalAlert>> AlertsAsync(IReadOnlyCollection<LabResult> results, CancellationToken ct)
    {
        if (results.Count == 0)
        {
            return [];
        }

        var orderIds = results.Select(r => r.LabOrderId).Distinct().ToList();
        var doctors = await db.Set<LabOrder>().AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.OrderingPractitionerId, ct);
        return results.Select(r => new CriticalAlert(
            DiagnosticKind.Laboratory, r.FacilityId, r.PatientId, doctors.GetValueOrDefault(r.LabOrderId), r.TestName,
            $"{r.Describe()} ({r.Flag})", $"/lab/orders/{r.LabOrderId}")).ToList();
    }

    private Error? Record(LabResult result, ResultEvaluationContext context, ResultEntry entry, DateTimeOffset now)
    {
        var capture = context.Capture(result, entry.Value, entry.Remarks, entry.Organism, entry.ColonyCount, instrumentCode: null);
        if (capture.IsFailure)
        {
            return capture.Error;
        }

        var recorded = result.Record(capture.Value, currentUser.UserId, now);
        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (result.ResultType == LabResultType.Culture && entry.Susceptibilities is { } susceptibilities)
        {
            ReplaceSusceptibilities(result, susceptibilities);
        }

        return null;
    }

    /// <summary>The antibiogram is replaced as a whole; earlier rows are soft-deleted so the audit trail keeps them.</summary>
    private void ReplaceSusceptibilities(LabResult result, IReadOnlyList<SusceptibilityDto> susceptibilities)
    {
        foreach (var old in result.Susceptibilities.ToList())
        {
            db.Remove(old);
            result.Susceptibilities.Remove(old);
        }

        var order = 0;
        foreach (var s in susceptibilities)
        {
            result.Susceptibilities.Add(new LabSusceptibility
            {
                LabResultId = result.Id,
                Antibiotic = s.Antibiotic.Trim(),
                Interpretation = s.Interpretation,
                Mic = string.IsNullOrWhiteSpace(s.Mic) ? null : s.Mic.Trim(),
                DisplayOrder = ++order,
            });
        }
    }

    private async Task<IReadOnlyList<ResultSheetLine>> LinesAsync(IReadOnlyCollection<LabResult> results, ResultEvaluationContext context, CancellationToken ct)
    {
        var ids = results.Select(r => r.Id).ToList();
        var itemIds = results.Select(r => r.LabOrderItemId).Distinct().ToList();
        var called = ids.Count == 0
            ? []
            : await db.Set<CriticalCallLog>().AsNoTracking()
                .Where(c => c.LabResultId != null && ids.Contains(c.LabResultId.Value))
                .Select(c => c.LabResultId!.Value).Distinct().ToListAsync(ct);
        var itemNames = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => itemIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.TestName, ct);

        return results
            .OrderBy(r => itemNames.GetValueOrDefault(r.LabOrderItemId))
            .ThenBy(r => r.DisplayOrder)
            .Select(r =>
            {
                var test = context.Test(r.LabTestId);
                var previous = context.PreviousFor(r);
                return new ResultSheetLine(
                    r.Id, r.LabOrderItemId, itemNames.GetValueOrDefault(r.LabOrderItemId, r.TestName), r.LabTestId, r.TestCode, r.TestName, r.GroupName,
                    r.DisplayOrder, r.ResultType, r.Status, r.ValueText, r.Unit, test?.Decimals ?? 0, test?.Options ?? [], r.Flag, r.IsCritical,
                    r.ReferenceText ?? context.ReferenceTextFor(r), r.PreviousValue ?? previous?.Value, r.PreviousResultAt ?? previous?.At,
                    r.DeltaPercent, r.DeltaExceeded, r.AutoValidated, r.InstrumentCode, r.Method, r.Remarks, r.Organism, r.ColonyCount,
                    r.Susceptibilities.Where(s => !s.IsDeleted).OrderBy(s => s.DisplayOrder)
                        .Select(s => new SusceptibilityDto(s.Antibiotic, s.Interpretation, s.Mic)).ToList(),
                    r.EnteredAt, r.VerifiedAt, called.Contains(r.Id), r.Version);
            })
            .ToList();
    }
}
