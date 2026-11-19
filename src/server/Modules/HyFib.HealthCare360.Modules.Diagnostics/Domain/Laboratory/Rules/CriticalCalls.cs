namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>A call documented for a lab result, as the critical call register keeps it: the finding told, and when.</summary>
public sealed record CriticalCallMade(string Finding, DateTimeOffset InformedAt, DateTimeOffset RecordedAt);

/// <summary>
/// Which critical-value calls count for a lab result (LIS-05): one made for the value the result has now. The register
/// keeps each call's finding as text, the test, the value with its unit and the flag (<see cref="Finding"/>), and the
/// last call is what the doctor or ward was last told. A result needs a call before it is verified when it is critical
/// and they were not last told this value; and, critical or not, when they were last told another value. A value phoned
/// and then entered again, rerun or reopened is phoned again before it is verified, so that the ward, which may have
/// acted on the first value, hears that it was wrong (as the amendment of a released report asks for a new call).
/// </summary>
internal static class CriticalCalls
{
    /// <summary>What a call about <paramref name="result"/> records: "Potassium 7.2 mmol/L (CriticalHigh)".</summary>
    public static string Finding(LabResult result) => $"{result.TestName} {result.Describe()} ({result.Flag})";

    /// <summary>
    /// What a call that corrects an earlier one records: the value as it is now, and what was told before.
    /// "Potassium 2.7 mmol/L (Low), correcting Potassium 7.2 mmol/L (CriticalHigh)".
    /// </summary>
    public static string Correcting(LabResult result, CriticalCallMade told) => $"{Finding(result)}, correcting {told.Finding}";

    /// <summary>
    /// Whether <paramref name="call"/> told <paramref name="result"/>'s value as it is now: the same test and value with its
    /// unit, whatever the flag beside it (critical limits changed since keep the value the ward heard).
    /// </summary>
    public static bool IsFor(CriticalCallMade call, LabResult result)
    {
        var told = $"{result.TestName} {result.Describe()} (";
        return call.Finding.StartsWith(told.Length > CriticalCallLog.FindingLength ? told[..CriticalCallLog.FindingLength] : told, StringComparison.Ordinal);
    }

    /// <summary>The call made last (by the time it was made, then recorded): what the doctor or ward was last told.</summary>
    public static CriticalCallMade? Last(IEnumerable<CriticalCallMade> calls) =>
        calls.OrderBy(c => c.InformedAt).ThenBy(c => c.RecordedAt).LastOrDefault();

    /// <summary>
    /// Whether <paramref name="result"/> needs a documented call before it is verified: they were last told another value,
    /// or, with no call yet, it is critical.
    /// </summary>
    public static bool NeedsCall(LabResult result, IReadOnlyCollection<CriticalCallMade> calls) =>
        Last(calls) is { } last ? !IsFor(last, result) : result.IsCritical;
}
