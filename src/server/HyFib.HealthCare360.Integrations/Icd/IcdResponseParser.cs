using System.Net;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Icd;

/// <summary>An ICD-10 category as the ICD-API returns it, with its child categories' paths.</summary>
internal sealed record Icd10Entity(string Code, string Title, string? ClassKind, IReadOnlyList<string> ChildPaths);

/// <summary>Parses WHO ICD-API v2 responses (ICD-11 MMS flat search and ICD-10 category lookups).</summary>
internal static partial class IcdResponseParser
{
    /// <summary>
    /// <c>{"destinationEntities":[{"theCode":"1A00","title":"Cholera","chapter":"01","isLeaf":true}]}</c>. Entities
    /// without a code (blocks, chapters) are skipped because they cannot be recorded as a diagnosis.
    /// </summary>
    public static IReadOnlyList<IcdSearchResult> ParseIcd11Search(string json, string release, int limit)
    {
        if (!IntegrationHttp.TryParseJson(json, out var document))
        {
            return [];
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("destinationEntities", out var entities) || entities.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return entities.EnumerateArray()
                .Select(e => (Code: IntegrationHttp.Property(e, "theCode"), Title: IntegrationHttp.Property(e, "title"), Chapter: IntegrationHttp.Property(e, "chapter"),
                    IsLeaf: !e.TryGetProperty("isLeaf", out var leaf) || leaf.ValueKind != JsonValueKind.False))
                .Where(e => !string.IsNullOrWhiteSpace(e.Code) && !string.IsNullOrWhiteSpace(e.Title))
                .Select(e => new IcdSearchResult(e.Code!.Trim(), CleanTitle(e.Title!), $"ICD-11 MMS {release}", e.Chapter, e.IsLeaf))
                .DistinctBy(r => r.Code)
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>
    /// <c>{"code":"E11","title":{"@value":"Type 2 diabetes mellitus"},"classKind":"category","child":["http://id.who.int/icd/release/10/2019/E11.0"]}</c>.
    /// Child links are reduced to their path so they are always fetched from the configured API host (never from a
    /// host named in a response).
    /// </summary>
    public static Icd10Entity? ParseIcd10Entity(string json)
    {
        if (!IntegrationHttp.TryParseJson(json, out var document))
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var code = IntegrationHttp.Property(root, "code");
            var title = root.TryGetProperty("title", out var titleElement)
                ? titleElement.ValueKind == JsonValueKind.Object ? IntegrationHttp.Property(titleElement, "@value") : IntegrationHttp.Text(titleElement)
                : null;
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var children = root.TryGetProperty("child", out var childArray) && childArray.ValueKind == JsonValueKind.Array
                ? childArray.EnumerateArray().Select(IntegrationHttp.Text).Select(ChildPath).OfType<string>().ToList()
                : [];
            return new Icd10Entity(code.Trim(), CleanTitle(title), IntegrationHttp.Property(root, "classKind"), children);
        }
    }

    public static bool IsIcd10Code(string text) => Icd10Code().IsMatch(text.Trim().ToUpperInvariant());

    /// <summary>Search results arrive with highlighting markup (<c>&lt;em class='found'&gt;</c>) and HTML entities.</summary>
    internal static string CleanTitle(string title) => WebUtility.HtmlDecode(Tags().Replace(title, string.Empty)).Trim();

    private static string? ChildPath(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        var path = parsed.AbsolutePath;
        return ChildPathPattern().IsMatch(path) ? path.TrimStart('/') : null;
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"^[A-Z][0-9]{2}(\.[0-9A-Z]{1,2})?$")]
    private static partial Regex Icd10Code();

    [GeneratedRegex(@"^/icd/release/10/[0-9]{4}/[A-Z][0-9]{2}(\.[0-9A-Z]{1,2})?$")]
    private static partial Regex ChildPathPattern();
}
