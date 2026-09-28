using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.AI.Features.Clinical;

namespace HyFib.HealthCare360.Modules.AI.Services.Clinical;

/// <summary>The sections and suggestions read from the model's structured answer.</summary>
internal sealed record ClinicalNoteContent(
    string Subjective,
    string Objective,
    string Assessment,
    string Plan,
    IReadOnlyList<SuggestedIcdCode> SuggestedIcd,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates the model's SOAP answer as untrusted input: bounded section lengths, only well-formed ICD-10 codes
/// (others are dropped rather than shown as if they were valid), confidences clamped to 0–1, at most five suggestions
/// and ten warnings.
/// </summary>
internal static partial class ClinicalNoteParser
{
    public const int MaxSectionLength = 4000;
    public const int MaxSuggestions = 5;
    public const int MaxWarnings = 10;

    public static ClinicalNoteContent? TryParse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ClinicalNoteContent(
                Section(root, "subjective"),
                Section(root, "objective"),
                Section(root, "assessment"),
                Section(root, "plan"),
                Suggestions(root),
                Warnings(root));
        }
    }

    private static string Section(JsonElement root, string name)
    {
        var text = root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        text = text.ReplaceLineEndings("\n").Trim();
        return text.Length > MaxSectionLength ? text[..MaxSectionLength] : text;
    }

    private static IReadOnlyList<SuggestedIcdCode> Suggestions(JsonElement root)
    {
        if (!root.TryGetProperty("suggestedIcd", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var suggestions = new List<SuggestedIcdCode>();
        foreach (var item in items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
        {
            var code = (item.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null)?.Trim().ToUpperInvariant();
            var title = (item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)?.Trim();
            if (code is null || !Icd10().IsMatch(code) || string.IsNullOrEmpty(title) || suggestions.Any(s => s.Code == code))
            {
                continue;
            }

            var confidence = item.TryGetProperty("confidence", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var number)
                ? Math.Round(Math.Clamp(number, 0, 1), 2)
                : 0;
            suggestions.Add(new SuggestedIcdCode(code, title.Length > 200 ? title[..200] : title, confidence));
            if (suggestions.Count == MaxSuggestions)
            {
                break;
            }
        }

        return suggestions;
    }

    private static IReadOnlyList<string> Warnings(JsonElement root) =>
        root.TryGetProperty("warnings", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
                .Where(w => w.ValueKind == JsonValueKind.String)
                .Select(w => (w.GetString() ?? string.Empty).Trim())
                .Where(w => w.Length > 0)
                .Select(w => w.Length > 300 ? w[..300] : w)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxWarnings)
                .ToList()
            : [];

    [GeneratedRegex(@"^[A-Z][0-9]{2}(\.[0-9A-Z]{1,4})?$")]
    private static partial Regex Icd10();
}
