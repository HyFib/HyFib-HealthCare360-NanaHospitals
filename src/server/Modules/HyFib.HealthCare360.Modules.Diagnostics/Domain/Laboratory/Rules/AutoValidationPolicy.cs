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
    bool ReadAgainstBothSexes = false);

public sealed record AutoValidationDecision(bool Approved, IReadOnlyList<string> Reasons);

/// <summary>
/// Auto-validation rules (LIS-04, pure; unit-tested). An analyser result is verified without a pathologist only if
/// every condition holds: it is numeric, flagged Normal against an applicable interval, today's QC for the assay is
/// accepted, the delta check passed, and the analyser raised no flag. Critical, abnormal or unflaggable results always
/// wait for a person, and so does a result read against both the male and the female interval (a patient recorded as
/// neither). The reasons explain to the pathologist why a result was held back.
/// </summary>
internal static class AutoValidationPolicy
{
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
            reasons.Add("Today's QC for this test is missing or not accepted.");
        }

        if (input.Delta.Exceeded)
        {
            reasons.Add("The delta check against the previous result failed.");
        }

        if (!IsBenignInstrumentFlag(input.InstrumentFlags))
        {
            reasons.Add($"The analyser flagged the result ({input.InstrumentFlags!.Trim()}).");
        }

        return new AutoValidationDecision(reasons.Count == 0, reasons);
    }

    /// <summary>HL7 OBX-8 / ASTM abnormal flags: empty or "N" (normal) is benign; H, L, A, &lt;, &gt;, W… are not.</summary>
    internal static bool IsBenignInstrumentFlag(string? flags) =>
        string.IsNullOrWhiteSpace(flags) || string.Equals(flags.Trim(), "N", StringComparison.OrdinalIgnoreCase);
}
