namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;

/// <summary>
/// A dialysis patient's infection status (HBV, HCV, HIV) decides the machine and the isolation they get, so it changes only
/// with a verified lab result that supports it, and taking a patient off a positive status waits for a second person
/// (maker-checker, slice 15). Putting a sero-negative patient on a positive status isolates them, the stricter direction,
/// and is made at once: an infected patient never waits on a shared machine for an approval.
/// </summary>
public static class InfectionStatusChange
{
    /// <summary>How old the supporting result may be: the re-screening interval.</summary>
    public const int MaxResultAgeDays = SeroSegregation.RescreeningIntervalDays;

    /// <summary>Whether going from <paramref name="current"/> to <paramref name="asked"/> waits for a second person: anything but negative to positive.</summary>
    public static bool Waits(SeroStatus current, SeroStatus asked) => current != asked && current != SeroStatus.Negative;

    /// <summary>
    /// The infection a supporting result must test for: the one asked for, or, to go back to sero-negative, the one the
    /// patient is positive for now.
    /// </summary>
    public static SeroStatus Tested(SeroStatus current, SeroStatus asked) => asked == SeroStatus.Negative ? current : asked;

    /// <summary>
    /// Whether a lab test (its code and name, as the laboratory's catalogue has them) tests for <paramref name="infection"/>:
    /// HBsAg for hepatitis B, anti-HCV or HCV RNA for hepatitis C, HIV 1/2 for HIV.
    /// </summary>
    public static bool TestsFor(SeroStatus infection, string testCode, string testName)
    {
        var text = $"{testCode} {testName}".ToUpperInvariant();
        return infection switch
        {
            SeroStatus.HBsAg => text.Contains("HBS", StringComparison.Ordinal) || text.Contains("HEPATITIS B", StringComparison.Ordinal),
            SeroStatus.HCV => text.Contains("HCV", StringComparison.Ordinal) || text.Contains("HEPATITIS C", StringComparison.Ordinal),
            SeroStatus.HIV => text.Contains("HIV", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>Whether the result, verified on <paramref name="verifiedOn"/>, is recent enough to decide the status today.</summary>
    public static bool Recent(DateOnly verifiedOn, DateOnly today) => verifiedOn <= today && today.DayNumber - verifiedOn.DayNumber <= MaxResultAgeDays;
}
