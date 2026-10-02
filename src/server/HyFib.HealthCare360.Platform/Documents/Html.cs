using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>
/// A piece of HTML that is safe to put into a document as it is. It is made only from template text written in the
/// code, with every value encoded on the way in (<see cref="Of"/>), so data can never turn into markup. The server's
/// document templates (printed copies and PDFs) build their pages from these.
/// </summary>
public readonly struct Html : IEquatable<Html>
{
    // Every script stays readable in the source (Hindi names are not turned into entities); only markup characters are encoded.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private readonly string? _value;

    private Html(string value) => _value = value;

    /// <summary>Numbers and dates in documents follow Indian conventions (1,23,456.00).</summary>
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-IN");

    public static Html Empty => default;

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>
    /// Template text with values put in encoded: <c>Html.Of($"&lt;td&gt;{patient.Name}&lt;/td&gt;")</c>. A value that is
    /// already <see cref="Html"/>, or a sequence of it, goes in as it is; anything else is formatted (en-IN) and encoded.
    /// </summary>
    public static Html Of(ref HtmlInterpolatedStringHandler html) => html.ToHtml();

    /// <summary>Plain text, encoded.</summary>
    public static Html Text(string? text) => text is null ? Empty : new Html(Encode(text));

    /// <summary>Text whose line breaks are kept.</summary>
    public static Html Lines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var lines = text.Trim().ReplaceLineEndings("\n").Split('\n');
        return new Html(string.Join("<br>", lines.Select(line => Encode(line.TrimEnd()))));
    }

    /// <summary>Text as paragraphs: each block of lines becomes a <c>&lt;p&gt;</c>, and line breaks inside it are kept.</summary>
    public static Html Paragraphs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var blocks = Regex.Split(text.Trim().ReplaceLineEndings("\n"), @"\n\s*\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        return Join(blocks.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => Of($"<p>{Lines(b)}</p>")));
    }

    public static Html Join(IEnumerable<Html> parts) => Join(parts, Empty);

    public static Html Join(IEnumerable<Html> parts, Html separator)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return new Html(string.Join(separator.ToString(), parts.Where(p => !p.IsEmpty).Select(p => p.ToString())));
    }

    /// <summary>An image from <see cref="ImageData"/>; nothing when there is no image.</summary>
    public static Html Image(ImageData? image, string alt, string? cssClass = null)
    {
        if (image is null)
        {
            return Empty;
        }

        // The data URI is checked when ImageData is made and holds no markup characters, so it goes in as it is.
        var classAttribute = cssClass is null ? "" : $" class=\"{Encode(cssClass)}\"";
        return new Html($"<img{classAttribute} src=\"{image.DataUri}\" alt=\"{Encode(alt)}\">");
    }

    /// <summary>
    /// Markup the server itself generated, such as an SVG from QRCoder. Never pass data through this: use <see cref="Of"/>.
    /// </summary>
    internal static Html Trusted(string markup) => new(markup);

    public static string Encode(string? text) => text is null ? "" : Encoder.Encode(text);

    public override string ToString() => _value ?? "";

    public bool Equals(Html other) => string.Equals(ToString(), other.ToString(), StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Html other && Equals(other);

    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(Html left, Html right) => left.Equals(right);

    public static bool operator !=(Html left, Html right) => !left.Equals(right);

    public static Html operator +(Html left, Html right) => new(left.ToString() + right.ToString());

    internal static Html FromHandler(string value) => new(value);
}

/// <summary>Builds <see cref="Html"/> from an interpolated string: the literal text is kept, every value is encoded.</summary>
[InterpolatedStringHandler]
public ref struct HtmlInterpolatedStringHandler
{
    private readonly StringBuilder _builder;

    public HtmlInterpolatedStringHandler(int literalLength, int formattedCount) => _builder = new StringBuilder(literalLength + (formattedCount * 16));

    public readonly void AppendLiteral(string value) => _builder.Append(value);

    public readonly void AppendFormatted<T>(T value) => Append(value, null);

    public readonly void AppendFormatted<T>(T value, string? format) => Append(value, format);

    internal readonly Html ToHtml() => Html.FromHandler(_builder.ToString());

    private readonly void Append(object? value, string? format)
    {
        switch (value)
        {
            case null:
                return;
            case Html html:
                _builder.Append(html.ToString());
                return;
            case IEnumerable<Html> many:
                foreach (var part in many)
                {
                    _builder.Append(part.ToString());
                }

                return;
            case string text:
                _builder.Append(Html.Encode(text));
                return;
            case IFormattable formattable:
                _builder.Append(Html.Encode(formattable.ToString(format, Html.Culture)));
                return;
            default:
                _builder.Append(Html.Encode(value.ToString()));
                return;
        }
    }
}

/// <summary>An image to embed in a document, as a checked <c>data:</c> URI (documents are rendered without network access).</summary>
public sealed partial class ImageData
{
    private ImageData(string dataUri) => DataUri = dataUri;

    public string DataUri { get; }

    /// <summary>The image, or null when its type is not PNG, JPEG, WebP or SVG.</summary>
    public static ImageData? FromBytes(ReadOnlySpan<byte> content, string contentType) =>
        content.IsEmpty || !Types.Contains(contentType) ? null : new ImageData($"data:{contentType};base64,{Convert.ToBase64String(content)}");

    /// <summary>An existing data URI, if it is an image of an allowed type in base64.</summary>
    public static ImageData? FromDataUri(string? dataUri) =>
        dataUri is not null && DataUriPattern().IsMatch(dataUri) ? new ImageData(dataUri) : null;

    private static readonly HashSet<string> Types = ["image/png", "image/jpeg", "image/webp", "image/svg+xml"];

    [GeneratedRegex(@"^data:image/(png|jpeg|webp|svg\+xml);base64,[A-Za-z0-9+/]+={0,2}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DataUriPattern();
}
