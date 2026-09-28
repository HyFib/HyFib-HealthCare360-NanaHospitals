using System.Globalization;

namespace HyFib.HealthCare360.Modules.AI.Services.Assistant;

/// <summary>Numbers as speech recognisers write them: "2", "two", "a", "a couple of", "twenty one".</summary>
internal static class SpokenNumbers
{
    private static readonly Dictionary<string, int> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["a"] = 1, ["an"] = 1, ["one"] = 1, ["single"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly Dictionary<string, int> Phrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a couple of"] = 2, ["couple of"] = 2, ["a couple"] = 2, ["a few"] = 3, ["few"] = 3,
    };

    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = string.Join(' ', text.Trim().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries));
        if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (Phrases.TryGetValue(normalized, out value) || Units.TryGetValue(normalized, out value) || Tens.TryGetValue(normalized, out value))
        {
            return true;
        }

        var words = normalized.Split(' ');
        if (words.Length == 2 && Tens.TryGetValue(words[0], out var tens) && Units.TryGetValue(words[1], out var unit) && unit is > 0 and < 10)
        {
            value = tens + unit;
            return true;
        }

        value = 0;
        return false;
    }
}
