namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

public enum LoadStatus
{
    Loaded = 0,
    Running,
    Completed,
    Released,
    Failed,
}

public enum IndicatorResult
{
    Pending = 0,
    Pass,
    Fail,
}

/// <summary>
/// One sterilizer cycle (batch/load) with its physical parameters and indicator results (CSSD-04). Packs are released
/// on a passed chemical indicator and valid parameters (parametric release) while the biological indicator incubates,
/// except loads with implants, which stay quarantined until it passes. A biological failure after release fails the
/// load and triggers a recall of every pack from it (CSSD-05).
/// </summary>
public sealed class SterilizationLoad : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string CycleNumber { get; set; } = string.Empty;
    public string SterilizerName { get; set; } = string.Empty;
    public SterilizationMethod Method { get; set; }
    public string? Program { get; set; }
    public bool ContainsImplants { get; set; }
    public DateTimeOffset LoadedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public decimal? TemperatureC { get; set; }
    public decimal? PressureKpa { get; set; }
    public int? ExposureMinutes { get; set; }
    public IndicatorResult ChemicalIndicator { get; set; }
    public IndicatorResult BiologicalIndicator { get; set; }
    public string? BiologicalIndicatorLot { get; set; }
    public DateTimeOffset? BiologicalReadAt { get; set; }
    public string? OperatorName { get; set; }
    public Guid? OperatorUserId { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public string? ReleasedByName { get; set; }
    public LoadStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public string? Notes { get; set; }
    public List<SterilePack> Packs { get; set; } = [];

    /// <summary>The load failed after its packs were released: they must be traced and recalled.</summary>
    public bool RequiresRecall => Status == LoadStatus.Failed && ReleasedAt is not null;

    public Result Start(DateTimeOffset now)
    {
        if (Status != LoadStatus.Loaded)
        {
            return Error.Rule("cssd.load_not_loaded", "Only a loaded cycle can be started.");
        }

        if (Packs.Count == 0)
        {
            return Error.Rule("cssd.load_empty", "Add at least one pack to the load.");
        }

        Status = LoadStatus.Running;
        StartedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Records the cycle printout. A cycle outside the validated parameters or with a failed chemical indicator fails at
    /// once and its packs go back for reprocessing; the result tells the caller which way it went.
    /// </summary>
    public Result Complete(decimal temperatureC, decimal? pressureKpa, int exposureMinutes, IndicatorResult chemicalIndicator, DateTimeOffset now)
    {
        if (Status != LoadStatus.Running)
        {
            return Error.Rule("cssd.load_not_running", "Only a running cycle can be completed.");
        }

        if (chemicalIndicator == IndicatorResult.Pending)
        {
            return Error.Rule("cssd.ci_required", "Read the chemical indicator before completing the cycle.");
        }

        TemperatureC = temperatureC;
        PressureKpa = pressureKpa;
        ExposureMinutes = exposureMinutes;
        ChemicalIndicator = chemicalIndicator;
        CompletedAt = now;

        var parameters = SterilizationParameters.Check(Method, temperatureC, exposureMinutes);
        if (!parameters.IsWithinRange)
        {
            MarkFailed($"Cycle parameters outside the validated range: {parameters.Message}", now);
        }
        else if (chemicalIndicator == IndicatorResult.Fail)
        {
            MarkFailed("Chemical indicator failed.", now);
        }
        else
        {
            Status = LoadStatus.Completed;
        }

        return Result.Success();
    }

    public Result Release(Guid? userId, string? userName, DateOnly today, DateTimeOffset now)
    {
        if (Status != LoadStatus.Completed)
        {
            return Error.Rule("cssd.load_not_releasable",
                Status == LoadStatus.Released ? "This load has already been released." : "Only a completed cycle with valid parameters can be released.");
        }

        if (ChemicalIndicator != IndicatorResult.Pass)
        {
            return Error.Rule("cssd.ci_not_passed", "The chemical indicator must pass before release.");
        }

        if (ContainsImplants && BiologicalIndicator != IndicatorResult.Pass)
        {
            return Error.Rule("cssd.implant_quarantine", "Loads with implants stay in quarantine until the biological indicator passes.");
        }

        Status = LoadStatus.Released;
        ReleasedAt = now;
        ReleasedBy = userId;
        ReleasedByName = userName;
        foreach (var pack in Packs)
        {
            pack.MarkSterile(today);
        }

        return Result.Success();
    }

    /// <summary>Records the biological indicator (read 1–48 hours after the cycle). A failure fails the load.</summary>
    public Result RecordBiologicalIndicator(IndicatorResult result, string? lot, DateTimeOffset now)
    {
        if (Status is LoadStatus.Loaded or LoadStatus.Running)
        {
            return Error.Rule("cssd.bi_too_early", "Read the biological indicator after the cycle has completed.");
        }

        if (result == IndicatorResult.Pending)
        {
            return Error.Rule("cssd.bi_result_required", "Record the biological indicator as passed or failed.");
        }

        if (BiologicalIndicator != IndicatorResult.Pending)
        {
            return Error.Rule("cssd.bi_recorded", "The biological indicator has already been recorded for this load.");
        }

        BiologicalIndicator = result;
        BiologicalIndicatorLot = string.IsNullOrWhiteSpace(lot) ? null : lot.Trim();
        BiologicalReadAt = now;
        if (result == IndicatorResult.Fail)
        {
            MarkFailed("Biological indicator failed.", now);
        }

        return Result.Success();
    }

    /// <summary>Operator aborts a cycle (power failure, door fault…) before release.</summary>
    public Result Abort(string reason, DateTimeOffset now)
    {
        if (Status is LoadStatus.Released or LoadStatus.Failed)
        {
            return Error.Rule("cssd.load_closed", "A released or failed load cannot be aborted. Record a failed biological indicator to recall a released load.");
        }

        MarkFailed(reason.Trim(), now);
        return Result.Success();
    }

    private void MarkFailed(string reason, DateTimeOffset now)
    {
        Status = LoadStatus.Failed;
        FailureReason = reason;
        CompletedAt ??= now;
        foreach (var pack in Packs.Where(p => p.Status == PackStatus.InProcess))
        {
            pack.Status = PackStatus.Reprocess;
        }
    }
}
