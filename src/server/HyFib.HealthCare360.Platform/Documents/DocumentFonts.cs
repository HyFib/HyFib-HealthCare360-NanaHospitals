using System.Text;

namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>
/// The fonts a document carries inside it, so that it prints the same on every computer and in the PDF browser whatever
/// fonts the machine has: Noto Sans Devanagari (SIL Open Font License 1.1, <c>Documents/Fonts/OFL.txt</c>), regular and
/// bold, cut down to the Devanagari blocks, for Hindi text. It is added only to a document that has Devanagari text, and
/// only for those characters (<c>unicode-range</c>), so Latin text keeps the house font. The browser lays the text out
/// (HarfBuzz), which forms the conjuncts and places the vowel signs.
/// </summary>
public static class DocumentFonts
{
    /// <summary>The family name the house style lists after Segoe UI.</summary>
    public const string Devanagari = "HC360 Devanagari";

    // Devanagari, Vedic extensions, Devanagari Extended, common Indic number forms, ZWNJ/ZWJ and the dotted circle the
    // shaper shows under a stray vowel sign.
    private const string DevanagariRange = "U+0900-097F, U+1CD0-1CFF, U+200C-200D, U+25CC, U+A830-A839, U+A8E0-A8FF";

    private static readonly Lazy<string> FontFaces = new(() =>
        Face("NotoSansDevanagari-Regular.woff2", 400) + Face("NotoSansDevanagari-Bold.woff2", 700));

    /// <summary>The @font-face rules to put in a document's style element when <paramref name="text"/> has Devanagari in it.</summary>
    public static string For(string text) => HasDevanagari(text) ? FontFaces.Value : "";

    public static bool HasDevanagari(string? text) =>
        text.AsSpan().ContainsAnyInRange('ऀ', 'ॿ') || text.AsSpan().ContainsAnyInRange('꣠', 'ꣿ');

    private static string Face(string file, int weight)
    {
        using var stream = typeof(DocumentFonts).Assembly.GetManifestResourceStream($"HyFib.HealthCare360.Platform.Documents.Fonts.{file}")
            ?? throw new InvalidOperationException($"The document font {file} is not embedded.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return new StringBuilder()
            .Append("@font-face { font-family: \"").Append(Devanagari).Append("\"; font-style: normal; font-weight: ").Append(weight)
            .Append("; font-display: swap; unicode-range: ").Append(DevanagariRange)
            .Append("; src: url(data:font/woff2;base64,").Append(Convert.ToBase64String(bytes.ToArray())).Append(") format(\"woff2\"); }\n")
            .ToString();
    }
}
