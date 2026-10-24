using System.Text;

namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>
/// The fonts a document carries inside it, so that it prints the same on every computer and in the PDF browser whatever
/// fonts the machine has: Noto Sans Devanagari for Hindi text and Noto Sans Tamil for Tamil text (SIL Open Font License
/// 1.1, <c>Documents/Fonts/OFL.txt</c>), regular and bold, each cut down to its script's blocks. A face is added only to
/// a document that has text in its script, and only for those characters (<c>unicode-range</c>), so Latin text keeps the
/// house font. The browser lays the text out (HarfBuzz), which forms the conjuncts and places the vowel signs.
/// </summary>
public static class DocumentFonts
{
    /// <summary>The family name the house style lists after Segoe UI for Hindi.</summary>
    public const string Devanagari = "HC360 Devanagari";

    /// <summary>The family name the house style lists after Segoe UI for Tamil.</summary>
    public const string Tamil = "HC360 Tamil";

    /// <summary>The house font list: Segoe UI, then the embedded faces, then Windows' own Indic font.</summary>
    public const string Families = "\"Segoe UI\", \"" + Devanagari + "\", \"" + Tamil + "\", \"Nirmala UI\"";

    // Devanagari, Vedic extensions, Devanagari Extended, common Indic number forms, ZWNJ/ZWJ and the dotted circle the
    // shaper shows under a stray vowel sign.
    private const string DevanagariRange = "U+0900-097F, U+1CD0-1CFF, U+200C-200D, U+25CC, U+A830-A839, U+A8E0-A8FF";

    // Tamil, the Tamil Supplement (fractions and old signs), the danda pair Tamil text borrows, ZWNJ/ZWJ and the dotted circle.
    private const string TamilRange = "U+0964-0965, U+0B80-0BFF, U+11FC0-11FFF, U+200C-200D, U+25CC";

    private static readonly Lazy<string> DevanagariFaces = new(() =>
        Face(Devanagari, DevanagariRange, "NotoSansDevanagari-Regular.woff2", 400) + Face(Devanagari, DevanagariRange, "NotoSansDevanagari-Bold.woff2", 700));

    private static readonly Lazy<string> TamilFaces = new(() =>
        Face(Tamil, TamilRange, "NotoSansTamil-Regular.woff2", 400) + Face(Tamil, TamilRange, "NotoSansTamil-Bold.woff2", 700));

    /// <summary>The @font-face rules to put in a document's style element for the scripts <paramref name="text"/> has.</summary>
    public static string For(string text) =>
        (HasDevanagari(text) ? DevanagariFaces.Value : "") + (HasTamil(text) ? TamilFaces.Value : "");

    public static bool HasDevanagari(string? text) =>
        text.AsSpan().ContainsAnyInRange('ऀ', 'ॿ') || text.AsSpan().ContainsAnyInRange('꣠', 'ꣿ');

    public static bool HasTamil(string? text) => text.AsSpan().ContainsAnyInRange('஀', '௿');

    private static string Face(string family, string range, string file, int weight)
    {
        using var stream = typeof(DocumentFonts).Assembly.GetManifestResourceStream($"HyFib.HealthCare360.Platform.Documents.Fonts.{file}")
            ?? throw new InvalidOperationException($"The document font {file} is not embedded.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return new StringBuilder()
            .Append("@font-face { font-family: \"").Append(family).Append("\"; font-style: normal; font-weight: ").Append(weight)
            .Append("; font-display: swap; unicode-range: ").Append(range)
            .Append("; src: url(data:font/woff2;base64,").Append(Convert.ToBase64String(bytes.ToArray())).Append(") format(\"woff2\"); }\n")
            .ToString();
    }
}
