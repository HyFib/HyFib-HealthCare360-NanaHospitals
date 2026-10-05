namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>A printer font chosen for a text height: its name, multiplier and real height in dots.</summary>
internal readonly record struct LabelFont(string Name, int Multiplier, int Height, int CellWidth);

/// <summary>
/// The fonts HC360 uses on raw labels, and how wide a text prints in them, so text is shortened before it reaches the
/// edge of the label.
/// <list type="bullet">
/// <item>ZPL: font 0 (CG Triumvirate Bold Condensed), scaled to any height. It is proportional, so widths are an
/// estimate per character class that errs on the wide side.</item>
/// <item>TSPL: the built-in dot fonts 1 to 5 (8×12 to 32×48 dots), magnified 1 to 3 times. They are fixed pitch, so widths
/// are exact; one dot per character is added because TSPL and TSPL2 printers space them slightly differently.</item>
/// </list>
/// </summary>
internal static class LabelFonts
{
    private static readonly (string Name, int Width, int Height)[] TsplDotFonts =
        [("1", 8, 12), ("2", 12, 20), ("3", 16, 24), ("4", 24, 32), ("5", 32, 48)];

    /// <summary>TSPL dot fonts and their magnifications, smallest first; a font at its own size before a magnified one.</summary>
    private static readonly LabelFont[] TsplChoices = TsplDotFonts
        .SelectMany(f => Enumerable.Range(1, 3).Select(m => new LabelFont(f.Name, m, f.Height * m, f.Width * m)))
        .OrderBy(f => f.Height)
        .ThenBy(f => f.Multiplier)
        .ToArray();

    /// <summary>
    /// The font for text about <paramref name="height"/> dots tall. ZPL scales font 0 to it; TSPL takes the dot font
    /// nearest in height (the smaller on a tie), never more than a quarter taller.
    /// </summary>
    public static LabelFont Pick(LabelLanguage language, int height)
    {
        if (language == LabelLanguage.Zpl)
        {
            var size = Math.Max(10, height);
            return new LabelFont("0", 1, size, size);
        }

        return TsplChoices
            .Where(f => f.Height * 4 <= height * 5)
            .OrderBy(f => Math.Abs(f.Height - height))
            .ThenBy(f => f.Height)
            .ThenBy(f => f.Multiplier)
            .DefaultIfEmpty(TsplChoices[0])
            .First();
    }

    /// <summary>Printed width of the text in dots (an upper estimate for ZPL font 0).</summary>
    public static int Measure(LabelLanguage language, string text, int height)
    {
        var font = Pick(language, height);
        if (language == LabelLanguage.Tspl)
        {
            return text.Length * (font.CellWidth + font.Multiplier);
        }

        var ems = 0.0;
        foreach (var c in text)
        {
            ems += Zpl0Width(c);
        }

        return (int)Math.Ceiling(ems * font.Height);
    }

    /// <summary>
    /// Width of a character of ZPL font 0 as a fraction of the font height when width equals height (<c>^A0N,h,h</c>).
    /// The font is a bold condensed sans serif; these are a little wider than its real advances.
    /// </summary>
    private static double Zpl0Width(char c) => c switch
    {
        'M' or 'W' or '@' or '%' => 0.86,
        'm' or 'w' => 0.80,
        'I' or 'i' or 'j' or 'l' or '!' or '|' or '\'' or '.' or ',' or ':' or ';' => 0.30,
        ' ' => 0.30,
        'f' or 't' or 'r' or '(' or ')' or '[' or ']' or '/' or '-' => 0.40,
        >= 'A' and <= 'Z' => 0.64,
        >= 'a' and <= 'z' => 0.56,
        >= '0' and <= '9' => 0.58,
        _ => 0.62,
    };

    /// <summary>
    /// The text, shortened with "..." when it is wider than <paramref name="width"/> dots. Returns an empty string when
    /// not even the first character and the dots fit.
    /// </summary>
    public static string Fit(LabelLanguage language, string text, int height, int width)
    {
        if (Measure(language, text, height) <= width)
        {
            return text;
        }

        for (var length = text.Length - 1; length > 0; length--)
        {
            var shortened = text[..length].TrimEnd() + "...";
            if (Measure(language, shortened, height) <= width)
            {
                return shortened;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The text in at most <paramref name="maxLines"/> lines of <paramref name="width"/> dots, broken between words where
    /// it can be; the last line ends in "..." when text is left over.
    /// </summary>
    public static IReadOnlyList<string> Wrap(LabelLanguage language, string text, int height, int width, int maxLines)
    {
        var lines = new List<string>();
        var rest = text.Trim();
        while (rest.Length > 0 && lines.Count < maxLines)
        {
            if (lines.Count == maxLines - 1 || Measure(language, rest, height) <= width)
            {
                lines.Add(Fit(language, rest, height, width));
                break;
            }

            var take = rest.Length;
            while (take > 1 && Measure(language, rest[..take], height) > width)
            {
                take--;
            }

            var space = rest.LastIndexOf(' ', Math.Min(take, rest.Length - 1));
            var cut = space > 0 ? space : take;
            lines.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
        }

        return lines;
    }
}
