namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>What the calculation step does to one result a formula fills (<see cref="CalculatedResults.Plan"/>).</summary>
internal abstract record CalculationStep
{
    /// <summary>Nothing changes.</summary>
    public static CalculationStep Nothing { get; } = new Leave();

    /// <summary>
    /// Nothing changes: the result was measured or typed in (that value stands), its inputs are not all there yet, or it
    /// holds this calculation already.
    /// </summary>
    public sealed record Leave : CalculationStep;

    /// <summary>
    /// The calculated value, as printed (rounded to the test's decimals), and the note that marks it calculated, with
    /// the formula and the inputs it was worked from. <paramref name="Reopen"/>: it was verified, and an input changed.
    /// </summary>
    public sealed record Fill(string ValueText, string Note, bool Reopen) : CalculationStep;

    /// <summary>
    /// It cannot be calculated: why, for whoever enters it. A value calculated before goes (the result is pending again;
    /// <paramref name="Reopen"/>: it was verified).
    /// </summary>
    public sealed record NotCalculated(string Why, bool Reopen) : CalculationStep;
}

/// <summary>
/// The calculation step of the result flow (pure; unit-tested). A result a formula fills (<see cref="ResultFormulas"/>)
/// is calculated from its inputs on the same order and sample, entered by hand or sent by an analyser:
/// <list type="bullet">
/// <item>It is stored as a result entered by nobody, marked calculated by its first held reason, the note
/// (<see cref="NotePrefix"/>): the formula and the inputs it was worked from, which the verifier reads. It is never
/// verified automatically: a person verifies it.</item>
/// <item>It is rounded to the test's decimals, then read against its reference interval like any result.</item>
/// <item>It is calculated again whenever an input changes before it is verified; an input corrected after it was
/// verified takes it back to unverified. When it can no longer be calculated (an input withdrawn, the triglycerides
/// now 400 mg/dL or more), the value it had goes and the result waits, with why.</item>
/// <item>A value measured (by an analyser) or typed in for it stands: the formula leaves it alone. So an analyser that
/// sends a measured VLDL wins over the calculation, as does a person who enters it by hand.</item>
/// </list>
/// </summary>
internal static class CalculatedResults
{
    /// <summary>The first words of the note that marks a result calculated.</summary>
    public const string NotePrefix = "Calculated by HC360: ";

    /// <summary>The first words of why a result a formula fills was not calculated.</summary>
    public const string NotCalculatedPrefix = "Not calculated: ";

    /// <summary>Whether HC360 calculated the result's value (its first held reason is the calculation's note).</summary>
    public static bool IsCalculated(LabResult result) =>
        result.HeldReasons.Count > 0 && result.HeldReasons[0].StartsWith(NotePrefix, StringComparison.Ordinal);

    /// <summary>
    /// What to do with <paramref name="result"/>, filled by <paramref name="formula"/>, given the results of its order and
    /// sample (<paramref name="sameSample"/>, which may include it). <paramref name="decimals"/> are its test's.
    /// </summary>
    public static CalculationStep Plan(LabResult result, ResultFormula formula, IReadOnlyCollection<LabResult> sameSample, int decimals)
    {
        var calculated = IsCalculated(result);
        if (result.Status != LabResultStatus.Pending && !calculated)
        {
            return CalculationStep.Nothing;
        }

        var verified = result.Status == LabResultStatus.Verified;
        var inputs = new Dictionary<string, FormulaInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in formula.Inputs)
        {
            var input = InputFor(result, term.Code, sameSample);
            if (input?.NumericValue is not { } value || input.ValueText is not { } text)
            {
                // Not entered yet: wait. An input whose value went (withdrawn) takes the value calculated from it along.
                return calculated
                    ? new CalculationStep.NotCalculated(NotCalculatedPrefix + $"{TermName(input, term)} has no value now.", verified)
                    : CalculationStep.Nothing;
            }

            if (text.TrimStart().StartsWith('<') || text.TrimStart().StartsWith('>'))
            {
                return Refuse(result, $"{input.TestName} is {text}, beyond what the analyser measures: it cannot be calculated from that.", verified);
            }

            if (!SameUnit(input.Unit, term.Unit))
            {
                return Refuse(result, $"{input.TestName} is in {input.Unit ?? "no unit"}; the formula is written for {term.Unit}.", verified);
            }

            inputs[term.Code] = new FormulaInput(term.Code, input.TestName, text, value, input.Unit);
        }

        if (formula.Unit is { } unit && !SameUnit(result.Unit, unit))
        {
            return Refuse(result, $"{result.TestName} is in {result.Unit ?? "no unit"}; the formula gives {unit}.", verified);
        }

        var values = new FormulaValues(inputs);
        var outcome = formula.Compute(values);
        if (outcome.Value is not { } computed)
        {
            return Refuse(result, outcome.NotCalculated ?? "The formula gave no value.", verified);
        }

        var valueText = ResultInterpreter.Format(computed, decimals);
        var note = $"{NotePrefix}{formula.Expression} = {formula.Worked(values)} = {valueText}{(string.IsNullOrEmpty(result.Unit) ? string.Empty : " " + result.Unit)}.";
        if (calculated && result.ValueText == valueText && result.HeldReasons[0] == note)
        {
            return CalculationStep.Nothing;
        }

        return new CalculationStep.Fill(valueText, note, verified);
    }

    /// <summary>
    /// The input result of <paramref name="code"/> for <paramref name="result"/>: on the same test ordered (a panel's
    /// own analyte) first, else anywhere on the order and sample.
    /// </summary>
    private static LabResult? InputFor(LabResult result, string code, IReadOnlyCollection<LabResult> sameSample)
    {
        var candidates = sameSample
            .Where(r => r.Id != result.Id && r.LabOrderId == result.LabOrderId && r.SampleId == result.SampleId
                        && string.Equals(r.TestCode, code, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return candidates.FirstOrDefault(r => r.LabOrderItemId == result.LabOrderItemId && r.Status != LabResultStatus.Pending)
               ?? candidates.FirstOrDefault(r => r.Status != LabResultStatus.Pending)
               ?? candidates.FirstOrDefault();
    }

    /// <summary>Why a result a formula fills was not calculated; nothing to change when it says so already.</summary>
    private static CalculationStep Refuse(LabResult result, string why, bool verified)
    {
        var text = NotCalculatedPrefix + why;
        return result.Status == LabResultStatus.Pending && result.HeldReasons is [var only] && only == text
            ? CalculationStep.Nothing
            : new CalculationStep.NotCalculated(text, verified);
    }

    private static string TermName(LabResult? input, FormulaTerm term) => input?.TestName ?? term.Code;

    private static bool SameUnit(string? unit, string? expected) =>
        string.Equals(AnalyserUnits.Canonical(unit), AnalyserUnits.Canonical(expected), StringComparison.Ordinal);
}
