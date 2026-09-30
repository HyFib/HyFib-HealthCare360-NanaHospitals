namespace HyFib.HealthCare360.Platform.Settings;

/// <summary>
/// How many years medical records are kept (SRS PRV-02: at least 3 for out-patient and 5 for in-patient charts, and
/// configurable because state rules and NABH may ask for more). The Privacy module's retention schedule edits these
/// settings; Clinical dates each chart with them when it is completed.
/// </summary>
public static class RecordRetention
{
    public const string OutpatientYearsSetting = "privacy.retention.records.outpatient";
    public const string InpatientYearsSetting = "privacy.retention.records.inpatient";
    public const int MinimumOutpatientYears = 3;
    public const int MinimumInpatientYears = 5;
    public const int MaximumYears = 99;

    /// <summary>The period in force for out-patient or in-patient charts, never below the minimum.</summary>
    public static async Task<int> YearsAsync(ISettingsService settings, bool inpatient, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync<int?>(inpatient ? InpatientYearsSetting : OutpatientYearsSetting, null, cancellationToken);
        var minimum = inpatient ? MinimumInpatientYears : MinimumOutpatientYears;
        return Math.Clamp(configured ?? minimum, minimum, MaximumYears);
    }
}
