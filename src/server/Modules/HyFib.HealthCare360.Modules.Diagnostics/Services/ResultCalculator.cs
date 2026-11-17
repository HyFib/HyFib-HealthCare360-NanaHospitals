using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>A result calculated again in an amendment, as it was and as it is now.</summary>
internal sealed record CalculatedAmendment(LabResult Result, string Before);

/// <summary>
/// The calculation step of the result flow (N7; <see cref="CalculatedResults"/> holds the rules). After results are
/// entered by hand, sent by an analyser or assigned from the unmatched results, the results a formula fills on the same
/// tubes are calculated again, in the same unit of work, which the caller saves. A result whose test is cancelled or
/// released, or whose tube is not in the lab, is left alone: a released one changes only with an amendment, which
/// calculates it again from the corrected values (<see cref="AmendCalculatedAsync"/>).
/// </summary>
internal sealed class ResultCalculator(HospitalDbContext db, ResultEvaluator evaluator, IAuditLogger audit, ILogger<ResultCalculator> logger)
{
    /// <summary>
    /// Calculates the results formulas fill on the tubes <paramref name="sampleIds"/> (tracked, not saved).
    /// </summary>
    /// <returns>The results it changed: calculated, calculated again, or taken back.</returns>
    public async Task<IReadOnlyList<LabResult>> CalculateAsync(IEnumerable<Guid?> sampleIds, DateTimeOffset now, CancellationToken ct)
    {
        var ids = sampleIds.OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        // Tracked on purpose: the caller has just changed some of them in this unit of work.
        await db.Set<LabResult>().Where(r => r.SampleId != null && ids.Contains(r.SampleId.Value)).LoadAsync(ct);
        var onTubes = db.Set<LabResult>().Local.Where(r => r.SampleId is { } id && ids.Contains(id)).ToList();
        var targets = ResultFormulas.InOrder(onTubes);
        if (targets.Count == 0)
        {
            return [];
        }

        var context = await evaluator.LoadAsync(targets, ct);
        var changed = new List<LabResult>();
        foreach (var result in targets)
        {
            if (ResultFormulas.For(result.TestCode) is not { } formula || context.Test(result.LabTestId) is not { } test
                || context.EnsureEditable(result).IsFailure)
            {
                continue;
            }

            var sameSample = onTubes.Where(r => r.SampleId == result.SampleId).ToList();
            switch (CalculatedResults.Plan(result, formula, sameSample, test.Decimals))
            {
                case CalculationStep.Fill fill:
                    var capture = context.Capture(result, fill.ValueText, remarks: null, organism: null, colonyCount: null, instrumentCode: null);
                    if (capture.IsFailure)
                    {
                        logger.LogWarning("The calculated value of {Test} on result {ResultId} is not one the test can hold: {Problem}",
                            result.TestCode, result.Id, capture.Error!.Message);
                        continue;
                    }

                    if (fill.Reopen)
                    {
                        Reopened(result, "an input it is calculated from was corrected");
                        result.Reopen();
                    }

                    result.Record(capture.Value, null, now);
                    result.HoldForReview([fill.Note]);
                    changed.Add(result);
                    break;

                case CalculationStep.NotCalculated refused:
                    if (refused.Reopen)
                    {
                        Reopened(result, "it cannot be calculated from its corrected inputs");
                    }

                    result.LeaveUncalculated(refused.Why);
                    changed.Add(result);
                    break;
            }
        }

        return changed;
    }

    /// <summary>
    /// For an amendment of released results (<paramref name="corrected"/>, already changed and tracked): the verified
    /// results of the order HC360 calculated from them are calculated again and amended with them, by
    /// <paramref name="by"/>, who verifies the whole correction. One that cannot be calculated from the corrected values
    /// is refused, for the pathologist to correct in the same amendment; one the amendment corrects by hand is theirs.
    /// </summary>
    public async Task<Result<IReadOnlyList<CalculatedAmendment>>> AmendCalculatedAsync(
        IReadOnlyCollection<LabResult> corrected, Guid orderId, Guid? by, DateTimeOffset now, CancellationToken ct)
    {
        var correctedIds = corrected.Select(r => r.Id).ToHashSet();
        var changedCodes = corrected.Select(r => r.TestCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await db.Set<LabResult>().Where(r => r.LabOrderId == orderId).LoadAsync(ct);
        var onOrder = db.Set<LabResult>().Local.Where(r => r.LabOrderId == orderId).ToList();
        var targets = ResultFormulas.InOrder(onOrder)
            .Where(r => !correctedIds.Contains(r.Id) && r.Status == LabResultStatus.Verified && CalculatedResults.IsCalculated(r))
            .ToList();
        if (targets.Count == 0)
        {
            return Result<IReadOnlyList<CalculatedAmendment>>.Ok([]);
        }

        var context = await evaluator.LoadAsync(targets, ct);
        var amended = new List<CalculatedAmendment>();
        foreach (var result in targets)
        {
            if (ResultFormulas.For(result.TestCode) is not { } formula || context.Test(result.LabTestId) is not { } test
                || !formula.Inputs.Any(i => changedCodes.Contains(i.Code)))
            {
                continue;
            }

            var sameSample = onOrder.Where(r => r.SampleId == result.SampleId).ToList();
            switch (CalculatedResults.Plan(result, formula, sameSample, test.Decimals))
            {
                case CalculationStep.Fill fill:
                    var capture = context.Capture(result, fill.ValueText, remarks: null, organism: null, colonyCount: null, instrumentCode: null);
                    if (capture.IsFailure)
                    {
                        return Error.Rule("lab.calculated_result", $"{result.TestName} cannot be calculated from the corrected results: {capture.Error!.Message} Correct it too.");
                    }

                    var before = result.Describe();
                    var made = result.Amend(capture.Value, by, now);
                    if (made.IsFailure)
                    {
                        return made.Error!;
                    }

                    result.HoldForReview([fill.Note]);
                    changedCodes.Add(result.TestCode);
                    amended.Add(new CalculatedAmendment(result, before));
                    break;

                case CalculationStep.NotCalculated refused:
                    return Error.Rule("lab.calculated_result",
                        $"{result.TestName} is calculated from the results corrected, and cannot be calculated from them now. "
                        + $"{refused.Why[CalculatedResults.NotCalculatedPrefix.Length..]} Correct it too, in the same amendment.");
            }
        }

        return Result<IReadOnlyList<CalculatedAmendment>>.Ok(amended);
    }

    private void Reopened(LabResult result, string why) =>
        audit.Add(new AuditEntry(AuditCategories.Clinical, "lab.result.reopened", $"Reopened {result.TestName} ({result.Describe()}): {why}")
        {
            PatientId = result.PatientId,
            EntityType = nameof(LabResult),
            EntityId = result.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
}
