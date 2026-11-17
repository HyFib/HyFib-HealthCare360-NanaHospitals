using System.Globalization;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>An analyte a formula reads, by test code, and the unit its value must be in.</summary>
internal sealed record FormulaTerm(string Code, string Unit);

/// <summary>A value a formula reads: the input's result as printed and as a number.</summary>
internal sealed record FormulaInput(string Code, string Name, string ValueText, decimal Value, string? Unit);

/// <summary>The values a formula is given: its inputs by test code, and the laboratory's barometric pressure (mmHg).</summary>
internal sealed record FormulaValues(IReadOnlyDictionary<string, FormulaInput> Inputs, decimal BarometricPressure)
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
/// A result HC360 calculates from others on the same order and sample, instead of measuring it (Nana Hospitals, N7 and
/// N8): the analyte it fills (by test code), the analytes it reads and the unit each must be in, the unit it gives, the
/// formula in words, the worked sum shown to the verifier, and the calculation. Formulas live here, keyed by test code,
/// not in the test master: the laboratory renames a test freely, but its code is what the formula means.
/// </summary>
internal sealed record ResultFormula(
    string Code,
    string Expression,
    IReadOnlyList<FormulaTerm> Inputs,
    string? Unit,
    Func<FormulaValues, string> Worked,
    Func<FormulaValues, FormulaOutcome> Compute)
{
    /// <summary>The formula reads the laboratory's barometric pressure (<see cref="ResultFormulas.BarometricPressureSetting"/>).</summary>
    public bool ReadsBarometricPressure { get; init; }
}

/// <summary>
/// The calculated results (pure; unit-tested with worked examples), keyed by the code of the analyte each fills:
/// <list type="bullet">
/// <item>Indirect bilirubin = total − direct; globulin = total protein − albumin; A/G ratio = albumin ÷ globulin.</item>
/// <item>VLDL = triglycerides ÷ 5, and LDL = total cholesterol − HDL − triglycerides ÷ 5, in mg/dL and only when the
/// triglycerides are below 400 mg/dL (Friedewald WT, Levy RI, Fredrickson DS, "Estimation of the concentration of
/// low-density lipoprotein cholesterol in plasma, without use of the preparative ultracentrifuge", Clin Chem
/// 1972;18(6):499-502). Above that they are left for a person.</item>
/// <item>The arterial blood gas's P/F ratio = pO2 ÷ (FiO2/100), and its alveolar-arterial gradient
/// = (FiO2/100 × (Patm − 47) − pCO2/0.8) − pO2: the alveolar gas equation (Curran-Everett D, "A classic learning
/// opportunity from Fenn, Rahn, and Otis (1946): the alveolar gas equation", Adv Physiol Educ 2006;30(2):58-62,
/// doi:10.1152/advan.00076.2005; Sood P, Paul G, Puri S, "Interpretation of arterial blood gas", Indian J Crit Care Med
/// 2010;14(2):57-64, doi:10.4103/0972-5229.68215), with water vapour at 37 °C (47 mmHg), a respiratory quotient of 0.8 and
/// Patm the laboratory's barometric pressure (<see cref="BarometricPressureSetting"/>). The FiO2 is the one entered for
/// the sample, from 21 % (room air) to 100 %: never assumed. The staff app offers 21 for a person to confirm.</item>
/// </list>
/// A formula never calculates from a value beyond the measuring range ("&lt;0.10"), nor from an input in another unit than
/// it is written for, and it refuses a result that cannot be (a direct bilirubin above the total, a gradient below zero):
/// each leaves the result for a person, with why.
/// </summary>
internal static class ResultFormulas
{
    public const string IndirectBilirubin = "IBIL";
    public const string Globulin = "GLOB";
    public const string AlbuminGlobulinRatio = "AGR";
    public const string Vldl = "VLDL";
    public const string Ldl = "LDL";
    public const string PfRatio = "ABG-PF";
    public const string AaGradient = "ABG-AAGRAD";

    public const string TotalBilirubin = "TBIL";
    public const string DirectBilirubin = "DBIL";
    public const string TotalProtein = "TP";
    public const string Albumin = "ALB";
    public const string Triglycerides = "TG";
    public const string Cholesterol = "CHOL";
    public const string Hdl = "HDL";
    public const string Po2 = "ABG-PO2";
    public const string Pco2 = "ABG-PCO2";
    public const string FiO2 = "ABG-FIO2";

    /// <summary>Friedewald's limit: from 400 mg/dL of triglycerides up, VLDL is not a fifth of them.</summary>
    public const decimal FriedewaldLimit = 400m;

    /// <summary>
    /// The facility setting that holds the laboratory's barometric pressure in mmHg (default <see cref="SeaLevelPressure"/>):
    /// about 733 mmHg at Theni, some 300 m above the sea.
    /// </summary>
    public const string BarometricPressureSetting = "lab.barometricPressureMmHg";

    /// <summary>The barometric pressure at sea level, mmHg: the gradient's Patm while the laboratory has set none.</summary>
    public const decimal SeaLevelPressure = 760m;

    /// <summary>Water vapour pressure at 37 °C, mmHg.</summary>
    public const decimal WaterVapour = 47m;

    /// <summary>The respiratory quotient the alveolar gas equation assumes.</summary>
    public const decimal RespiratoryQuotient = 0.8m;

    /// <summary>Room air's FiO2, %: what the result sheet offers for a person to confirm, never what HC360 assumes.</summary>
    public const string RoomAirFiO2 = "21";

    private const decimal LowestPressure = 400m;
    private const decimal HighestPressure = 800m;

    private const string MgDl = "mg/dL";
    private const string GDl = "g/dL";
    private const string MmHg = "mmHg";
    private const string Percent = "%";

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

        new(PfRatio, "pO2 ÷ (FiO2/100)",
            [new(Po2, MmHg), new(FiO2, Percent)], MmHg,
            v => $"{v.Text(Po2)} ÷ {Fraction(v[FiO2])}",
            v => FiO2Problem(v) is { } problem ? FormulaOutcome.Refused(problem) : FormulaOutcome.Of(v[Po2] / (v[FiO2] / 100m))),

        new(AaGradient, "(FiO2/100 × (Patm − 47) − pCO2/0.8) − pO2",
            [new(Po2, MmHg), new(Pco2, MmHg), new(FiO2, Percent)], MmHg,
            v => $"({Fraction(v[FiO2])} × ({Number(v.BarometricPressure)} − 47) − {v.Text(Pco2)}/0.8) − {v.Text(Po2)}",
            AlveolarArterialGradient)
        {
            ReadsBarometricPressure = true,
        },
    }.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>The formula that fills the analyte <paramref name="code"/>, or null when it is measured.</summary>
    public static ResultFormula? For(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>
    /// A value the result sheet offers for a person to confirm before it is saved (the FiO2: <see cref="RoomAirFiO2"/>),
    /// or null. HC360 never stores it, nor calculates from it, until a person enters it.
    /// </summary>
    public static string? SuggestedValue(string code) =>
        string.Equals(code, FiO2, StringComparison.OrdinalIgnoreCase) ? RoomAirFiO2 : null;

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

    private static FormulaOutcome AlveolarArterialGradient(FormulaValues v)
    {
        if (FiO2Problem(v) is { } problem)
        {
            return FormulaOutcome.Refused(problem);
        }

        if (v.BarometricPressure is < LowestPressure or > HighestPressure)
        {
            return FormulaOutcome.Refused($"The laboratory's barometric pressure ({BarometricPressureSetting}) is {Number(v.BarometricPressure)} mmHg, "
                                          + $"not between {Number(LowestPressure)} and {Number(HighestPressure)}: correct the setting.");
        }

        var alveolar = (v[FiO2] / 100m * (v.BarometricPressure - WaterVapour)) - (v[Pco2] / RespiratoryQuotient);
        var gradient = alveolar - v[Po2];
        return gradient < 0m
            ? FormulaOutcome.Refused("The gradient comes out below zero: check the FiO2 entered, the pO2 and the pCO2.")
            : FormulaOutcome.Of(gradient);
    }

    /// <summary>Why the FiO2 entered cannot be calculated from: below room air's 21 % (a fraction typed, 0.40) or above 100 %.</summary>
    private static string? FiO2Problem(FormulaValues v) =>
        v[FiO2] is < 21m or > 100m
            ? $"The FiO2 entered, {v.Text(FiO2)} %, is not between 21 (room air) and 100: enter it in % (40, not 0.40)."
            : null;

    private static string Fraction(decimal fio2) => Number(fio2 / 100m);

    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
