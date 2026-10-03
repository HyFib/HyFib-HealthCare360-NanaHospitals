using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// Text as a label printer's own fonts can print it. ZPL font 0 and the TSPL dot fonts cover printable ASCII, so
/// accents are dropped (José → Jose), the typographic marks staff screens use become their ASCII forms (– → -, ° → deg),
/// and white space collapses to single spaces. Letters with no ASCII form (Devanagari, say) cannot be printed this way:
/// <see cref="Printable"/> refuses them, and the staff app prints that label through the Windows driver instead.
/// </summary>
internal static class LabelText
{
    private static readonly Dictionary<char, string> Replacements = new()
    {
        ['·'] = "-", // middle dot, as in "EDTA · HAEM"
        ['•'] = "-", // bullet
        ['×'] = "x", // multiplication sign
        ['‐'] = "-",
        ['‑'] = "-",
        ['‒'] = "-",
        ['–'] = "-",
        ['—'] = "-",
        ['−'] = "-", // minus sign, as in "O−"
        ['‘'] = "'",
        ['’'] = "'",
        ['′'] = "'",
        ['“'] = "\"",
        ['”'] = "\"",
        ['…'] = "...",
        ['₹'] = "Rs ",
        ['°'] = "deg ",
        [' '] = " ",
    };

    /// <summary>
    /// The value in printable ASCII, trimmed, with runs of white space as one space; empty for null or blank.
    /// </summary>
    /// <param name="field">What the value is ("The patient's name"), for the message when it cannot be printed.</param>
    /// <exception cref="LabelTextException">A letter has no printable form.</exception>
    public static string Printable(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = text.Length > 0;
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                // The accent of a Latin letter, now separate from its letter. A mark on a letter of another script
                // (a Devanagari vowel sign, say) belongs to a letter refused below.
                continue;
            }

            var replacement = c is >= ' ' and <= '~' ? null : Replacements.GetValueOrDefault(c);
            if (c is < ' ' or > '~' && replacement is null)
            {
                throw new LabelTextException($"{field} has letters the label printer's own fonts cannot print.");
            }

            if (space)
            {
                text.Append(' ');
                space = false;
            }

            text.Append(replacement ?? c.ToString());
        }

        return text.ToString().Trim();
    }
}
