namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>
/// The analyser's abnormal flags as the gateway passes them on (HL7 OBX-8 repetitions, ASTM R.7 repeats, joined with
/// commas). <c>HH</c> and <c>LL</c> (above or below the panic limits, in HL7 table 0078 and ASTM E1394 alike) and
/// <c>AA</c> (critically abnormal, HL7) say the analyser found the value critical, whatever the LIS makes of it.
/// </summary>
internal static class AnalyserFlags
{
    private static readonly HashSet<string> CriticalFlags = new(StringComparer.OrdinalIgnoreCase) { "HH", "LL", "AA" };

    /// <summary>Between flags (the gateway's commas), and before a flag's text where an analyser sends it as a component.</summary>
    private static readonly char[] Separators = [',', '^', ' '];

    /// <summary>Whether any of the flags is critical: <c>HH</c>, <c>LL</c> or <c>AA</c>, in any case and among others.</summary>
    public static bool IsCritical(string? flags) =>
        !string.IsNullOrWhiteSpace(flags)
        && flags.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(CriticalFlags.Contains);
}
