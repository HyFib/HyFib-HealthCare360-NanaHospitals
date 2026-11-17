namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>An analyte a formula reads, by test code, and the unit its value must be in.</summary>
internal sealed record FormulaTerm(string Code, string Unit);

/// <summary>A value a formula reads: the input's result as printed and as a number.</summary>
internal sealed record FormulaInput(string Code, string Name, string ValueText, decimal Value, string? Unit);

/// <summary>The values a formula is given: its inputs by test code.</summary>
internal sealed record FormulaValues(IReadOnlyDictionary<string, FormulaInput> Inputs)
{
    public decimal this[string code] => Inputs[code].Value;

    /// <summary>An input as printed, for the worked sum the verifier reads ("1.20").</summary>
    public string Text(string code) => Inputs[code].ValueText;
}

/// <summary>What a formula gives: a value, or why it was not calculated (left for a person).</summary>
internal sealed record FormulaOutcome(decimal? Value, string? NotCalculated)
{
    public static FormulaOutcome Of(decimal value) => new(value, null);

    public static FormulaOutcome Refused(string why) => new(null, why);
}

/// <summary>
/// A result HC360 calculates from others on the same order and sample, instead of measuring it (Nana Hospitals, N7):
/// the analyte it fills (by test code), the analytes it reads and the unit each must be in, the unit it gives, the
/// formula in words, the worked sum shown to the verifier, and the calculation. Formulas live here, keyed by test code,
/// not in the test master: the laboratory renames a test freely, but its code is what the formula means.
/// </summary>
internal sealed record ResultFormula(
    string Code,
    string Expression,
    IReadOnlyList<FormulaTerm> Inputs,
    string? Unit,
    Func<FormulaValues, string> Worked,
    Func<FormulaValues, FormulaOutcome> Compute);

/// <summary>
/// The calculated results (pure; unit-tested with worked examples), keyed by the code of the analyte each fills:
/// <list type="bullet">
/// <item>Indirect bilirubin = total − direct; globulin = total protein − albumin; A/G ratio = albumin ÷ globulin.</item>
/// <item>VLDL = triglycerides ÷ 5, and LDL = total cholesterol − HDL − triglycerides ÷ 5, in mg/dL and only when the
/// triglycerides are below 400 mg/dL (Friedewald WT, Levy RI, Fredrickson DS, "Estimation of the concentration of
/// low-density lipoprotein cholesterol in plasma, without use of the preparative ultracentrifuge", Clin Chem
/// 1972;18(6):499-502). Above that they are left for a person.</item>
/// </list>
/// A formula never calculates from a value beyond the measuring range ("&lt;0.10"), nor from an input in another unit than
/// it is written for, and it refuses a result that cannot be (a direct bilirubin above the total): each leaves the result
/// for a person, with why.
/// </summary>
internal static class ResultFormulas
{
    public const string IndirectBilirubin = "IBIL";
    public const string Globulin = "GLOB";
    public const string AlbuminGlobulinRatio = "AGR";
    public const string Vldl = "VLDL";
    public const string Ldl = "LDL";

    public const string TotalBilirubin = "TBIL";
    public const string DirectBilirubin = "DBIL";
    public const string TotalProtein = "TP";
    public const string Albumin = "ALB";
    public const string Triglycerides = "TG";
    public const string Cholesterol = "CHOL";
    public const string Hdl = "HDL";

    /// <summary>Friedewald's limit: from 400 mg/dL of triglycerides up, VLDL is not a fifth of them.</summary>
    public const decimal FriedewaldLimit = 400m;

    private const string MgDl = "mg/dL";
    private const string GDl = "g/dL";

    public static IReadOnlyDictionary<string, ResultFormula> ByCode { get; } = new ResultFormula[]
    {
        new(IndirectBilirubin, "Bilirubin, total − Bilirubin, direct",
            [new(TotalBilirubin, MgDl), new(DirectBilirubin, MgDl)], MgDl,
            v => $"{v.Text(TotalBilirubin)} − {v.Text(DirectBilirubin)}",
            v => v[DirectBilirubin] > v[TotalBilirubin]
                ? FormulaOutcome.Refused("The direct bilirubin is above the total: check both.")
                : FormulaOutcome.Of(v[TotalBilirubin] - v[DirectBilirubin])),

        new(Globulin, "Total protein − Albumin",
            [new(TotalProtein, GDl), new(Albumin, GDl)], GDl,
            v => $"{v.Text(TotalProtein)} − {v.Text(Albumin)}",
            v => v[Albumin] >= v[TotalProtein]
                ? FormulaOutcome.Refused("The albumin is not below the total protein: check both.")
                : FormulaOutcome.Of(v[TotalProtein] - v[Albumin])),

        new(AlbuminGlobulinRatio, "Albumin ÷ Globulin",
            [new(Albumin, GDl), new(Globulin, GDl)], null,
            v => $"{v.Text(Albumin)} ÷ {v.Text(Globulin)}",
            v => v[Globulin] <= 0m
                ? FormulaOutcome.Refused("The globulin is not above zero: check the total protein and the albumin.")
                : FormulaOutcome.Of(v[Albumin] / v[Globulin])),

        new(Vldl, "Triglycerides ÷ 5 (Friedewald)",
            [new(Triglycerides, MgDl)], MgDl,
            v => $"{v.Text(Triglycerides)} ÷ 5",
            v => v[Triglycerides] >= FriedewaldLimit
                ? FormulaOutcome.Refused($"The triglycerides are {v.Text(Triglycerides)} mg/dL: from 400 up, VLDL is not calculated (Friedewald). Measure it, or enter it by hand.")
                : FormulaOutcome.Of(v[Triglycerides] / 5m)),

        new(Ldl, "Total cholesterol − HDL − Triglycerides ÷ 5 (Friedewald)",
            [new(Cholesterol, MgDl), new(Hdl, MgDl), new(Triglycerides, MgDl)], MgDl,
            v => $"{v.Text(Cholesterol)} − {v.Text(Hdl)} − {v.Text(Triglycerides)} ÷ 5",
            FriedewaldLdl),
    }.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>The formula that fills the analyte <paramref name="code"/>, or null when it is measured.</summary>
    public static ResultFormula? For(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>
    /// The calculated results of one order and sample in the order they are calculated in: a result another one reads
    /// (globulin, for the A/G ratio) before it.
    /// </summary>
    public static IReadOnlyList<LabResult> InOrder(IEnumerable<LabResult> results) =>
        [.. results.Where(r => ByCode.ContainsKey(r.TestCode)).OrderBy(r => Depth(r.TestCode)).ThenBy(r => r.DisplayOrder)];

    /// <summary>1 for a formula that reads measured results only, one more for each calculated result it reads.</summary>
    private static int Depth(string code) =>
        ByCode.TryGetValue(code, out var formula) ? 1 + formula.Inputs.Select(i => Depth(i.Code)).DefaultIfEmpty(0).Max() : 0;

    private static FormulaOutcome FriedewaldLdl(FormulaValues v)
    {
        if (v[Triglycerides] >= FriedewaldLimit)
        {
            return FormulaOutcome.Refused($"The triglycerides are {v.Text(Triglycerides)} mg/dL: from 400 up, LDL is not calculated (Friedewald). Measure it, or enter it by hand.");
        }

        var ldl = v[Cholesterol] - v[Hdl] - (v[Triglycerides] / 5m);
        return ldl < 0m
            ? FormulaOutcome.Refused("HDL and a fifth of the triglycerides add up to more than the total cholesterol: check them.")
            : FormulaOutcome.Of(ldl);
    }
}
