namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Laboratory units as printed on HC360 reports ("10³/µL", "mm/hr") mapped to UCUM codes. A quantity is marked as UCUM
/// only when its unit is known here; any other unit is sent as display text without a system, never guessed.
/// </summary>
internal static class UcumUnits
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
    {
        ["%"] = "%",
        ["g/dL"] = "g/dL",
        ["g/L"] = "g/L",
        ["mg/dL"] = "mg/dL",
        ["mg/L"] = "mg/L",
        ["µg/dL"] = "ug/dL",
        ["ug/dL"] = "ug/dL",
        ["mcg/dL"] = "ug/dL",
        ["ng/dL"] = "ng/dL",
        ["ng/mL"] = "ng/mL",
        ["pg/mL"] = "pg/mL",
        ["pg"] = "pg",
        ["fL"] = "fL",
        ["mmol/L"] = "mmol/L",
        ["µmol/L"] = "umol/L",
        ["umol/L"] = "umol/L",
        ["mEq/L"] = "meq/L",
        ["U/L"] = "U/L",
        ["IU/L"] = "[IU]/L",
        ["mIU/L"] = "m[IU]/L",
        ["µIU/mL"] = "u[IU]/mL",
        ["uIU/mL"] = "u[IU]/mL",
        ["mIU/mL"] = "m[IU]/mL",
        ["10³/µL"] = "10*3/uL",
        ["10^3/µL"] = "10*3/uL",
        ["10^3/uL"] = "10*3/uL",
        ["x10³/µL"] = "10*3/uL",
        ["10⁶/µL"] = "10*6/uL",
        ["10^6/µL"] = "10*6/uL",
        ["10^6/uL"] = "10*6/uL",
        ["/µL"] = "/uL",
        ["/uL"] = "/uL",
        ["cells/µL"] = "/uL",
        ["/hpf"] = "/[HPF]",
        ["/lpf"] = "/[LPF]",
        ["mm/hr"] = "mm/h",
        ["mm/h"] = "mm/h",
        ["seconds"] = "s",
        ["sec"] = "s",
        ["s"] = "s",
        ["mL/min/1.73m²"] = "mL/min/{1.73_m2}",
        ["mL/min/1.73m2"] = "mL/min/{1.73_m2}",
        ["mg/24h"] = "mg/(24.h)",
    };

    /// <summary>The UCUM code for a printed unit, or null when it is not known.</summary>
    public static string? Code(string? unit) =>
        unit is null ? null : Codes.TryGetValue(unit.Trim(), out var code) ? code : null;
}
