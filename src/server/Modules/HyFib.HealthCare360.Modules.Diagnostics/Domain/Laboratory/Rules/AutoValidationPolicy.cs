namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>Facts about an analyser result needed to decide whether it may bypass manual verification.</summary>
public sealed record AutoValidationInput(
    LabResultType ResultType,
    ResultFlag? Flag,
    bool TestAllowsAutoValidation,
    bool InstrumentAllowsAutoValidation,
    bool QcAcceptedToday,
    DeltaOutcome Delta,
    string? InstrumentFlags,
    bool ReadAgainstBothSexes = false)
{
    /// <summary>The unit the analyser sent with the value; null when it sent none.</summary>
    public string? InstrumentUnit { get; init; }

    /// <summary>The test master's unit, the unit its reference intervals are written in.</summary>
    public string? TestUnit { get; init; }

    /// <summary>The analyser's result status (ASTM R.9, HL7 OBX-11); null when it sent none.</summary>
    public string? InstrumentStatus { get; init; }
}

public sealed record AutoValidationDecision(bool Approved, IReadOnlyList<string> Reasons);

/// <summary>
/// Auto-validation rules (LIS-04, pure; unit-tested). An analyser result is verified without a pathologist only if
/// every condition holds: it is numeric, flagged Normal against an applicable interval, today's QC for the assay on
/// that analyser is accepted, the delta check passed, and the analyser raised no flag, sent the value in the test's
/// unit and reported it as final. Critical, abnormal or unflaggable results always wait for a person, and so does a
/// result read against both the male and the female interval (a patient recorded as neither). The reasons are stored
/// on the result and tell the pathologist why it was held back.
/// </summary>
internal static class AutoValidationPolicy
{
    /// <summary>The status an analyser gives a final result, in ASTM E1394 and HL7 v2 alike.</summary>
    private const string FinalStatus = "F";

    public static AutoValidationDecision Evaluate(AutoValidationInput input)
    {
        var reasons = new List<string>();

        if (!input.TestAllowsAutoValidation)
        {
            reasons.Add("Auto-validation is switched off for this test.");
        }

        if (!input.InstrumentAllowsAutoValidation)
        {
            reasons.Add("Auto-validation is switched off for this analyser.");
        }

        if (input.ResultType != LabResultType.Numeric)
        {
            reasons.Add("Only numeric results are auto-validated.");
        }

        if (input.Flag != ResultFlag.Normal)
        {
            reasons.Add(input.Flag is { } flag ? $"The result is flagged {flag}." : "No reference interval applies to this patient.");
        }

        if (input.ReadAgainstBothSexes)
        {
            reasons.Add("The patient is recorded as neither male nor female, so the result was read against both the male and the female interval.");
        }

        if (!input.QcAcceptedToday)
        {
            reasons.Add("Today's QC for this test on this analyser is missing or not accepted.");
        }

        if (input.Delta.Exceeded)
        {
            reasons.Add("The delta check against the previous result failed.");
        }

        if (!IsBenignInstrumentFlag(input.InstrumentFlags))
        {
            reasons.Add($"The analyser flagged the result ({input.InstrumentFlags!.Trim()}).");
        }

        if (!IsFinal(input.InstrumentStatus))
        {
            reasons.Add($"The analyser did not report the result as final (status {input.InstrumentStatus!.Trim()}).");
        }

        if (!IsSameUnit(input.InstrumentUnit, input.TestUnit))
        {
            reasons.Add(string.IsNullOrWhiteSpace(input.TestUnit)
                ? $"The analyser sent the value in {input.InstrumentUnit!.Trim()}, and the test has no unit to compare it with."
                : $"The analyser sent the value in {input.InstrumentUnit!.Trim()}, not in the test's unit, {input.TestUnit.Trim()}.");
        }

        return new AutoValidationDecision(reasons.Count == 0, reasons);
    }

    /// <summary>HL7 OBX-8 / ASTM abnormal flags: empty or "N" (normal) is benign; H, L, A, &lt;, &gt;, W… are not.</summary>
    internal static bool IsBenignInstrumentFlag(string? flags) =>
        string.IsNullOrWhiteSpace(flags) || string.Equals(flags.Trim(), "N", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Final (F), or no status at all, which many ASTM analysers send for every result. Preliminary (P), partial (S),
    /// a correction (C), a result not yet verified on the analyser (HL7 R) or anything else is not taken as final.
    /// </summary>
    internal static bool IsFinal(string? status) =>
        string.IsNullOrWhiteSpace(status) || string.Equals(status.Trim(), FinalStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The analyser's unit is the test's, read alike whatever the case, the spaces and the spelling of the micro sign
    /// (µ, μ or u): "mg/dl" is "mg/dL", "µIU/mL" is "uIU/mL". An analyser that sent no unit has nothing to compare.
    /// </summary>
    internal static bool IsSameUnit(string? instrumentUnit, string? testUnit) =>
        string.IsNullOrWhiteSpace(instrumentUnit) || string.Equals(UnitKey(instrumentUnit), UnitKey(testUnit), StringComparison.Ordinal);

    private static string UnitKey(string? unit) =>
        string.Concat((unit ?? string.Empty).Where(c => !char.IsWhiteSpace(c)))
            .Replace('µ', 'u')
            .Replace('μ', 'u')
            .ToUpperInvariant();
}
