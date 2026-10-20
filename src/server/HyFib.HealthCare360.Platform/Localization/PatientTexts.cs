using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Platform.Localization;

/// <summary>
/// The text HC360 writes for patients, in every language of <see cref="PatientLanguages"/> (SRS PP-06). Each language is
/// a JSON file of key → text embedded in this assembly (<c>Localization/Resources/patient-texts-{code}.json</c>), so a
/// translator works on one file and a new language is one more file. Text may hold named placeholders
/// (<c>{doctor}</c>), and a translation has the same ones.
/// <list type="bullet">
/// <item><c>doc.*</c>: the labels of the documents patients receive (bills, prescriptions, reports, discharge summaries).</item>
/// <item><c>message.*</c>: the English text of a server message (a Problem Details title or detail, or a field
/// message), with its translation. Messages are matched on their English text, placeholders standing for any value, so a
/// message whose English is changed in the code is shown in English until its entry is updated, never with a stale
/// translation.</item>
/// </list>
/// </summary>
public sealed partial class PatientTexts
{
    private const string MessagePrefix = "message.";
    private const string ResourcePrefix = "HyFib.HealthCare360.Platform.Localization.Resources.patient-texts-";

    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _texts;
    private readonly FrozenDictionary<string, string> _exactMessages;
    private readonly IReadOnlyList<(Regex Pattern, string Key)> _patternMessages;

    internal PatientTexts(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> texts)
    {
        _texts = texts.ToFrozenDictionary(l => l.Key, l => l.Value.ToFrozenDictionary(StringComparer.Ordinal), StringComparer.Ordinal);
        var english = _texts[PatientLanguages.English];
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var patterns = new List<(Regex, string)>();
        foreach (var (key, text) in english.Where(e => e.Key.StartsWith(MessagePrefix, StringComparison.Ordinal)).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (Placeholders(text).Count == 0)
            {
                exact.TryAdd(text, key);
            }
            else
            {
                patterns.Add((PatternOf(text), key));
            }
        }

        _exactMessages = exact.ToFrozenDictionary(StringComparer.Ordinal);
        _patternMessages = patterns;
    }

    /// <summary>The texts shipped with HC360.</summary>
    public static PatientTexts Catalog { get; } = Load();

    /// <summary>Every language's texts by key (the English set is the complete one).</summary>
    public IReadOnlyDictionary<string, FrozenDictionary<string, string>> Languages => _texts;

    /// <summary>
    /// The text of <paramref name="key"/> in <paramref name="language"/>, in English when that language lacks it (the tests
    /// keep every language complete), with the placeholders filled in. A key English does not have is a mistake in the code.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such text.</exception>
    public string Text(string language, string key, params ReadOnlySpan<(string Name, string? Value)> values)
    {
        var text = _texts.TryGetValue(PatientLanguages.OrDefault(language), out var texts) && texts.TryGetValue(key, out var translated)
            ? translated
            : _texts[PatientLanguages.English].TryGetValue(key, out var english) ? english : throw new KeyNotFoundException($"No patient text '{key}'.");
        return values.Length == 0 ? text : Fill(text, values);
    }

    /// <summary>
    /// A server message (written in English in the code) in <paramref name="language"/>, or null when there is no
    /// translation of it (or the language is English).
    /// </summary>
    public string? TranslateMessage(string language, string? english)
    {
        if (string.IsNullOrEmpty(english) || language == PatientLanguages.English || !_texts.TryGetValue(language, out var texts))
        {
            return null;
        }

        if (_exactMessages.TryGetValue(english, out var key))
        {
            return texts.GetValueOrDefault(key);
        }

        foreach (var (pattern, patternKey) in _patternMessages)
        {
            var match = pattern.Match(english);
            if (match.Success && texts.TryGetValue(patternKey, out var translated))
            {
                return Fill(translated, [.. Placeholders(_texts[PatientLanguages.English][patternKey]).Select(name => (name, (string?)match.Groups[name].Value))]);
            }
        }

        return null;
    }

    /// <summary>The placeholder names in a text, in order ("{doctor} is away" → doctor).</summary>
    public static IReadOnlyList<string> Placeholders(string text) =>
        [.. PlaceholderRegex().Matches(text).Select(m => m.Groups[1].Value)];

    private static string Fill(string text, ReadOnlySpan<(string Name, string? Value)> values)
    {
        var filled = new StringBuilder(text);
        foreach (var (name, value) in values)
        {
            filled.Replace("{" + name + "}", value ?? "");
        }

        return filled.ToString();
    }

    // "{doctor} is not available." → ^(?<doctor>.+?) is not available\.$
    private static Regex PatternOf(string english)
    {
        var pattern = new StringBuilder("^");
        var last = 0;
        foreach (Match placeholder in PlaceholderRegex().Matches(english))
        {
            pattern.Append(Regex.Escape(english[last..placeholder.Index])).Append("(?<").Append(placeholder.Groups[1].Value).Append(">.+?)");
            last = placeholder.Index + placeholder.Length;
        }

        pattern.Append(Regex.Escape(english[last..])).Append('$');
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
    }

    private static PatientTexts Load()
    {
        var assembly = typeof(PatientTexts).Assembly;
        var texts = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var code in PatientLanguages.Codes)
        {
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + code + ".json")
                ?? throw new InvalidOperationException($"The patient texts for '{code}' are not embedded.");
            texts[code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException($"The patient texts for '{code}' are empty.");
        }

        return new PatientTexts(texts);
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}
