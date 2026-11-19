namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

/// <summary>
/// Reads a frequency the ward writes in words ("BD", "b.d.", "Once daily", "1-0-1", "Q6H", "SOS") as a prescription line
/// carries it, so a ward's order is checked as an e-prescription line of the same medicine would be: a frequency of the
/// prescription's list is that frequency; anything else is a custom one, whose doses a day the dose check reads from the
/// text ("1-0-1", "8 hourly"), or reports it could not.
/// </summary>
public static class WardFrequency
{
    private static readonly Dictionary<string, DoseFrequency> Known = new(StringComparer.Ordinal)
    {
        ["OD"] = DoseFrequency.OD,
        ["QD"] = DoseFrequency.OD,
        ["DAILY"] = DoseFrequency.OD,
        ["ONCEDAILY"] = DoseFrequency.OD,
        ["ONCEADAY"] = DoseFrequency.OD,
        ["QAM"] = DoseFrequency.OD,
        ["BD"] = DoseFrequency.BD,
        ["BID"] = DoseFrequency.BD,
        ["TWICEDAILY"] = DoseFrequency.BD,
        ["TWICEADAY"] = DoseFrequency.BD,
        ["TDS"] = DoseFrequency.TDS,
        ["TID"] = DoseFrequency.TDS,
        ["QID"] = DoseFrequency.QID,
        ["QDS"] = DoseFrequency.QID,
        ["HS"] = DoseFrequency.HS,
        ["NOCTE"] = DoseFrequency.HS,
        ["QHS"] = DoseFrequency.HS,
        ["SOS"] = DoseFrequency.SOS,
        ["PRN"] = DoseFrequency.SOS,
        ["ASNEEDED"] = DoseFrequency.SOS,
        ["ASREQUIRED"] = DoseFrequency.SOS,
        ["STAT"] = DoseFrequency.STAT,
        ["NOW"] = DoseFrequency.STAT,
        ["IMMEDIATELY"] = DoseFrequency.STAT,
        ["ONCE"] = DoseFrequency.STAT,
        ["WEEKLY"] = DoseFrequency.Weekly,
        ["ONCEWEEKLY"] = DoseFrequency.Weekly,
        ["ONCEAWEEK"] = DoseFrequency.Weekly,
    };

    /// <summary>The prescription's frequency for <paramref name="text"/>, and the text itself when it is a custom one.</summary>
    public static (DoseFrequency Frequency, string? Custom) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (DoseFrequency.Custom, null);
        }

        var compact = new string([.. text.Where(c => !char.IsWhiteSpace(c) && c is not '.' and not '-' and not '_')]).ToUpperInvariant();
        return Known.TryGetValue(compact, out var known) ? (known, null) : (DoseFrequency.Custom, text.Trim());
    }
}
