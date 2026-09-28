using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

internal sealed record PreviousResult(decimal Value, DateTimeOffset At);

/// <summary>
/// Loads everything needed to interpret a batch of results in a few queries: analytes with their reference
/// intervals, tubes (for the age at collection), items, patients, each patient's previous verified value per analyte
/// (delta check), and the facility's delta settings.
/// </summary>
internal sealed class ResultEvaluator(HospitalDbContext db, DiagnosticsLookups lookups, ISettingsService settings, TimeProvider time)
{
    /// <summary>Facility setting: default delta-check threshold in % when the test has none (default 50).</summary>
    public const string DeltaPercentSetting = "lab.deltaCheckPercent";

    /// <summary>Facility setting: how far back (days) to look for the previous result (default 30).</summary>
    public const string DeltaWindowSetting = "lab.deltaCheckWindowDays";

    public const decimal DefaultDeltaPercent = 50m;
    public const int DefaultDeltaWindowDays = 30;
    private const int MaxHistoryRows = 1000;

    public async Task<ResultEvaluationContext> LoadAsync(IReadOnlyCollection<LabResult> results, CancellationToken ct)
    {
        var testIds = results.Select(r => r.LabTestId).Distinct().ToList();
        var sampleIds = results.Select(r => r.SampleId).OfType<Guid>().Distinct().ToList();
        var itemIds = results.Select(r => r.LabOrderItemId).Distinct().ToList();
        var patientIds = results.Select(r => r.PatientId).Distinct().ToList();
        var orderIds = results.Select(r => r.LabOrderId).Distinct().ToList();

        var tests = await db.Set<LabTest>().AsNoTracking().Include(t => t.Ranges).Where(t => testIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        // Tracked on purpose: the caller may already have changed a tube in this unit of work (an analyser auto-receipt).
        await db.Set<LabSample>().Where(s => sampleIds.Contains(s.Id)).LoadAsync(ct);
        await db.Set<LabOrderItem>().Where(i => itemIds.Contains(i.Id)).LoadAsync(ct);
        var samples = db.Set<LabSample>().Local.Where(s => sampleIds.Contains(s.Id)).ToDictionary(s => s.Id);
        var items = db.Set<LabOrderItem>().Local.Where(i => itemIds.Contains(i.Id)).ToDictionary(i => i.Id);
        var patients = await lookups.PatientsAsync(patientIds, ct);

        Guid? facilityId = results.Count > 0 ? results.First().FacilityId : null;
        var deltaPercent = await settings.GetAsync<decimal?>(DeltaPercentSetting, facilityId, ct) ?? DefaultDeltaPercent;
        var windowDays = await settings.GetAsync<int?>(DeltaWindowSetting, facilityId, ct) ?? DefaultDeltaWindowDays;
        var since = time.GetUtcNow().AddDays(-Math.Clamp(windowDays, 1, 3650));

        var history = await db.Set<LabResult>().AsNoTracking()
            .Where(r => patientIds.Contains(r.PatientId) && testIds.Contains(r.LabTestId) && r.Status == LabResultStatus.Verified
                        && r.NumericValue != null && r.VerifiedAt >= since && !orderIds.Contains(r.LabOrderId))
            .OrderByDescending(r => r.VerifiedAt)
            .Take(MaxHistoryRows)
            .Select(r => new { r.PatientId, r.LabTestId, r.NumericValue, r.VerifiedAt })
            .ToListAsync(ct);
        var previous = history
            .GroupBy(h => (h.PatientId, h.LabTestId))
            .ToDictionary(g => g.Key, g => new PreviousResult(g.First().NumericValue!.Value, g.First().VerifiedAt!.Value));

        return new ResultEvaluationContext(tests, samples, items, patients, previous, deltaPercent, DiagnosticsTime.Today(time));
    }
}

/// <summary>Interprets results of one batch against the loaded context (reference interval, flag, delta).</summary>
internal sealed class ResultEvaluationContext(
    IReadOnlyDictionary<Guid, LabTest> tests,
    IReadOnlyDictionary<Guid, LabSample> samples,
    IReadOnlyDictionary<Guid, LabOrderItem> items,
    IReadOnlyDictionary<Guid, PatientSnapshot> patients,
    IReadOnlyDictionary<(Guid PatientId, Guid LabTestId), PreviousResult> previous,
    decimal defaultDeltaPercent,
    DateOnly today)
{
    public LabTest? Test(Guid labTestId) => tests.GetValueOrDefault(labTestId);

    public LabSample? Sample(Guid? sampleId) => sampleId is { } id ? samples.GetValueOrDefault(id) : null;

    public PreviousResult? PreviousFor(LabResult result) => previous.GetValueOrDefault((result.PatientId, result.LabTestId));

    /// <summary>Results can be entered while the tube is in the lab and the test is neither cancelled nor released.</summary>
    public Result EnsureEditable(LabResult result)
    {
        if (!items.TryGetValue(result.LabOrderItemId, out var item) || !item.IsActive)
        {
            return Error.Rule("lab.item_cancelled", $"{result.TestName} was cancelled.");
        }

        if (item.Status == LabOrderItemStatus.Released)
        {
            return Error.Rule("lab.result_released", $"{result.TestName} is released; amend the report to change it.");
        }

        return Sample(result.SampleId) is { Status: SampleStatus.Received or SampleStatus.InProcess or SampleStatus.Completed }
            ? Result.Success()
            : Error.Rule("lab.sample_not_in_lab", $"The sample for {result.TestName} has not been received in the lab.");
    }

    /// <summary>The interval for this patient: sex and age on the day the specimen was collected.</summary>
    public ReferenceRange? RangeFor(LabResult result)
    {
        if (Test(result.LabTestId) is not { } test)
        {
            return null;
        }

        var patient = patients.GetValueOrDefault(result.PatientId);
        var onDate = Sample(result.SampleId)?.CollectedAt is { } collected ? DiagnosticsTime.LocalDate(collected) : today;
        return ReferenceRangeSelector.Select(
            test.Ranges, patient?.Gender ?? Gender.Unknown, ReferenceRangeSelector.AgeInDays(patient?.DateOfBirth, onDate));
    }

    public string? ReferenceTextFor(LabResult result) =>
        Test(result.LabTestId) is { } test ? ResultInterpreter.ReferenceText(RangeFor(result), test.Decimals) : null;

    public Result<ResultCapture> Capture(LabResult result, string? value, string? remarks, string? organism, string? colonyCount, string? instrumentCode)
    {
        if (Test(result.LabTestId) is not { } test)
        {
            return Error.NotFound(nameof(LabTest), result.LabTestId);
        }

        var interpreted = ResultInterpreter.Interpret(test, RangeFor(result), value, organism);
        if (interpreted.IsFailure)
        {
            return interpreted.Error!;
        }

        var before = PreviousFor(result);
        var delta = DeltaCheck.Evaluate(interpreted.Value.NumericValue, before?.Value, before?.At, test.DeltaThresholdPercent ?? defaultDeltaPercent);
        return new ResultCapture(
            interpreted.Value,
            delta,
            TextLimits.ClipOrNull(remarks, 1000),
            test.ResultType == LabResultType.Culture ? TextLimits.ClipOrNull(organism, 200) : null,
            test.ResultType == LabResultType.Culture ? TextLimits.ClipOrNull(colonyCount, 100) : null,
            test.Method,
            instrumentCode);
    }
}
