using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Common;

internal enum TemplateEncoding
{
    None = 0,
    Url,
    Json,
}

/// <summary>
/// <c>{{placeholder}}</c> handling for administrator-configured request templates (generic SMS gateways, viewer URLs)
/// and for recovering template variables from already-rendered notification text (MSG91 Flow API).
/// </summary>
internal static partial class TemplateRenderer
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Replaces placeholders with values encoded for where they land (query string, JSON string literal), so a
    /// patient name containing quotes or ampersands can neither break nor inject into the request. Unknown
    /// placeholders render as empty.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values, TemplateEncoding encoding) =>
        Placeholder().Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? Encode(value ?? string.Empty, encoding) : string.Empty);

    /// <summary>
    /// Recovers placeholder values from text that was rendered from <paramref name="template"/>. Literal parts are
    /// matched exactly and anchored, placeholders lazily; a placeholder used twice must repeat the same value.
    /// </summary>
    public static bool TryExtract(string template, string rendered, out IReadOnlyDictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pattern = new StringBuilder("^");
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var last = 0;
        foreach (Match match in Placeholder().Matches(template))
        {
            pattern.Append(Regex.Escape(template[last..match.Index]));
            var name = match.Groups[1].Value;
            if (groups.TryGetValue(name, out var group))
            {
                pattern.Append(@"\k<").Append(group).Append('>');
            }
            else
            {
                group = "p" + groups.Count.ToString(CultureInfo.InvariantCulture);
                groups[name] = group;
                pattern.Append("(?<").Append(group).Append(">.*?)");
            }

            last = match.Index + match.Length;
        }

        pattern.Append(Regex.Escape(template[last..])).Append('$');

        try
        {
            var result = Regex.Match(rendered, pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, MatchTimeout);
            if (!result.Success)
            {
                return false;
            }

            var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, group) in groups)
            {
                extracted[name] = result.Groups[group].Value;
            }

            values = extracted;
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string Encode(string value, TemplateEncoding encoding) => encoding switch
    {
        TemplateEncoding.Url => Uri.EscapeDataString(value),
        // "Relaxed" only affects HTML-sensitive characters, which are harmless in a JSON body sent server-to-server.
        TemplateEncoding.Json => JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
        _ => value,
    };

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    private static partial Regex Placeholder();
}
