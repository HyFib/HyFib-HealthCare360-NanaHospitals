using System.Text;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>
/// Whether the unit an analyser sent is the test's unit written another way (pure; unit-tested). Analysers spell the
/// same unit in many ways: "10^3/uL", "x10³/µL", "10*3/uL" (UCUM), "K/uL", "10^9/L" and "/cumm" are all the test's
/// 10³/µL; "s", "sec" and "seconds" are one unit; "mg%" is mg/dL. Two spellings are the same unit when they name the same quantity in
/// the same scale, so the value needs no conversion: 10⁹ per litre is 10³ per microlitre, and a cubic millimetre is a
/// microlitre. Units of another scale (g/L against g/dL, mmol/L against mg/dL, mEq/L against mmol/L) are never taken as
/// the same. The staff app's review hints read units with the same rule (<c>isSameUnit</c> in lab/flags.ts); both are
/// tested against the same list of spellings (lab/unitSpellings.json).
/// </summary>
internal static class AnalyserUnits
{
    /// <summary>Superscript digits, as analysers and the catalogue write powers of ten (10³, 10⁶, 10¹²).</summary>
    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    /// <summary>Spellings of one unit, after <see cref="Canonical"/> has read the notation alike (keys and values).</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // Thousands per microlitre (white cells, platelets).
        ["K/UL"] = "10^3/UL",
        ["THOU/UL"] = "10^3/UL",
        ["THOUSAND/UL"] = "10^3/UL",
        ["THOUSANDS/UL"] = "10^3/UL",
        ["10^9/L"] = "10^3/UL",

        // Millions per microlitre (red cells).
        ["M/UL"] = "10^6/UL",
        ["MILL/UL"] = "10^6/UL",
        ["MILLION/UL"] = "10^6/UL",
        ["MILLIONS/UL"] = "10^6/UL",
        ["10^12/L"] = "10^6/UL",

        // Lakhs (10⁵) per microlitre, as Indian reports write platelets.
        ["LAKH/UL"] = "10^5/UL",
        ["LAKHS/UL"] = "10^5/UL",

        // Seconds (prothrombin time, APTT).
        ["SEC"] = "S",
        ["SECS"] = "S",
        ["SECOND"] = "S",
        ["SECONDS"] = "S",

        // Millimetres in the first hour (ESR).
        ["MM/H"] = "MM/HR",
        ["MM/1STHR"] = "MM/HR",

        // Per cent by weight in volume, as Indian reports write sugar (mg%) and haemoglobin (gm%): per 100 mL is per dL.
        ["MG%"] = "MG/DL",
        ["GM%"] = "G/DL",
        ["G%"] = "G/DL",
        ["GM/DL"] = "G/DL",

        // Enzyme activity: the international unit is the unit of the clinical enzyme assays.
        ["IU/L"] = "U/L",
    };

    /// <summary>
    /// The analyser's unit is the test's, or the analyser sent none (nothing to compare). A test with no unit matches
    /// only an analyser that sent none.
    /// </summary>
    public static bool AreSame(string? instrumentUnit, string? testUnit) =>
        string.IsNullOrWhiteSpace(instrumentUnit)
        || string.Equals(Canonical(instrumentUnit), Canonical(testUnit), StringComparison.Ordinal);

    /// <summary>
    /// One spelling for every way of writing a unit: no spaces, upper case, the micro sign as u, powers of ten as 10^n
    /// (10³, 10*3, 10E3, x10^3), a cubic millimetre (mm³, cumm) as a microlitre, then the aliases above.
    /// </summary>
    internal static string Canonical(string? unit)
    {
        var text = new StringBuilder();
        var inExponent = false;
        foreach (var c in unit ?? string.Empty)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            var superscript = Superscripts.IndexOf(c, StringComparison.Ordinal);
            if (superscript >= 0)
            {
                if (!inExponent)
                {
                    text.Append('^');
                }

                text.Append((char)('0' + superscript));
                inExponent = true;
                continue;
            }

            inExponent = false;
            text.Append(c switch
            {
                'µ' or 'μ' => 'u',
                '×' => 'x',
                _ => c,
            });
        }

        var key = text.ToString().ToUpperInvariant();

        // A leading multiplication sign ("x10^3/uL") says only that the value is a multiple of the power of ten.
        if (key.StartsWith("X10", StringComparison.Ordinal))
        {
            key = key[1..];
        }

        // UCUM writes 10*3, some analysers 10E3: both are 10^3.
        if (key.StartsWith("10*", StringComparison.Ordinal) || (key.StartsWith("10E", StringComparison.Ordinal) && key.Length > 3 && char.IsAsciiDigit(key[3])))
        {
            key = "10^" + key[3..];
        }

        // A cubic millimetre is a microlitre: "/cumm", "/mm3" and "/mm^3" are "/uL".
        key = key.Replace("/CUMM", "/UL", StringComparison.Ordinal)
            .Replace("/MM^3", "/UL", StringComparison.Ordinal)
            .Replace("/MM3", "/UL", StringComparison.Ordinal);

        // "Cells/uL" and "cells/cumm" count cells per microlitre, as the bare "/uL" does.
        if (key.StartsWith("CELLS/", StringComparison.Ordinal))
        {
            key = key["CELLS".Length..];
        }

        return Aliases.TryGetValue(key, out var alias) ? alias : key;
    }
}
