using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.SharedKernel;

/// <summary>
/// The state or union territory an address names, as its GST state code (<see cref="Gstin.States"/>), however it is
/// commonly written: the GST name with any spacing, capitals or punctuation ("Tamilnadu", "TAMIL NADU."), "&amp;" for
/// "and", a note in brackets ("Puducherry (U.T.)"), a former or longer name (Orissa, Pondicherry, Uttaranchal, NCT of
/// Delhi), or a two-letter code of ISO 3166-2:IN or of vehicle registration, old or new ("TN", "U.P.", "OD", "OR").
/// </summary>
public static partial class IndianStates
{
    private static readonly Dictionary<string, string> Codes = Build();

    /// <summary>The GST state code of the state <paramref name="name"/> names, or null when it names none.</summary>
    public static string? Code(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = Bracketed().Replace(name, " ");
        return Codes.GetValueOrDefault(Key(text.Replace("&", " and ", StringComparison.Ordinal))) ?? Codes.GetValueOrDefault(Key(text));
    }

    /// <summary>The state's name as GST writes it ("TN" → "Tamil Nadu"), or null when <paramref name="name"/> names none.</summary>
    public static string? Name(string? name) => Code(name) is { } code ? Gstin.States[code] : null;

    // Letters only, in lower case: "U.P." and "up", "Tamil Nadu" and "TAMILNADU" are the same.
    private static string Key(string text) => string.Concat(text.Where(char.IsAsciiLetter)).ToLowerInvariant();

    private static Dictionary<string, string> Build()
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, name) in Gstin.States)
        {
            codes[Key(name)] = code;
        }

        (string Code, string[] Names)[] others =
        [
            ("01", ["JK"]),
            ("02", ["HP"]),
            ("03", ["PB"]),
            ("04", ["CH"]),
            ("05", ["UK", "UT", "Uttaranchal"]),
            ("06", ["HR"]),
            ("07", ["DL", "New Delhi", "NCT of Delhi", "National Capital Territory of Delhi"]),
            ("08", ["RJ"]),
            ("09", ["UP"]),
            ("10", ["BR"]),
            ("11", ["SK"]),
            ("12", ["AR"]),
            ("13", ["NL"]),
            ("14", ["MN"]),
            ("15", ["MZ"]),
            ("16", ["TR"]),
            ("17", ["ML"]),
            ("18", ["AS"]),
            ("19", ["WB"]),
            ("20", ["JH"]),
            ("21", ["OD", "OR", "Orissa"]),
            ("22", ["CG", "CT"]),
            ("23", ["MP"]),
            ("24", ["GJ"]),
            ("26", ["DH", "DD", "DN", "Dadra and Nagar Haveli", "Daman and Diu"]),
            ("27", ["MH"]),
            ("29", ["KA"]),
            ("30", ["GA"]),
            ("31", ["LD"]),
            ("32", ["KL"]),
            ("33", ["TN"]),
            ("34", ["PY", "Pondicherry"]),
            ("35", ["AN", "Andaman and Nicobar"]),
            ("36", ["TS", "TG"]),
            ("37", ["AP"]),
            ("38", ["LA"]),
        ];
        foreach (var (code, names) in others)
        {
            foreach (var name in names)
            {
                codes.Add(Key(name), code);
            }
        }

        return codes;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Bracketed();
}
