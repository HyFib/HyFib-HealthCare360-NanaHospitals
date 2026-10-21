using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.AI.Services.Privacy;

/// <summary>Identifiers known for the patient in context: names (patient, guardian, emergency contact) and ids/phones.</summary>
internal sealed record RedactionContext(IReadOnlyList<string> Names, IReadOnlyList<string> Identifiers)
{
    public static RedactionContext Empty { get; } = new([], []);
}

/// <summary>Redacted text, the placeholder → original map (kept in memory only) and counts per category.</summary>
internal sealed partial class RedactionResult(IReadOnlyList<string> texts, IReadOnlyDictionary<string, string> placeholders, IReadOnlyDictionary<string, int> counts)
{
    /// <summary>The redacted text (the first, when several were redacted together).</summary>
    public string Text => Texts.Count > 0 ? Texts[0] : string.Empty;

    /// <summary>Each redacted text, in the order given.</summary>
    public IReadOnlyList<string> Texts { get; } = texts;

    public IReadOnlyDictionary<string, string> Placeholders { get; } = placeholders;

    public IReadOnlyDictionary<string, int> Counts { get; } = counts;

    public int Total => Counts.Values.Sum();

    /// <summary>
    /// Puts original values back where a model echoed placeholders (command parameters such as a UHID). Clinical note
    /// drafts are never restored: the clinician sees the placeholders and nothing identifying was generated.
    /// </summary>
    public string Restore(string value) =>
        Placeholder().Replace(value, match => Placeholders.TryGetValue(match.Value, out var original) ? original : match.Value);

    [GeneratedRegex(@"\[[A-Z]+_\d+\]")]
    private static partial Regex Placeholder();
}

/// <summary>
/// Removes direct identifiers before text is sent to an AI provider, in the cloud or in the hospital (SEC-12, ADR-011),
/// through <see cref="RedactedAiRequest"/>: e-mail and ABHA
/// addresses, UHIDs (keyword-introduced and in the <c>{FAC}{YY}{SEQ}</c> format), ABHA/Aadhaar-like 14- and 12-digit
/// groups, Indian mobile and landline numbers, other long digit runs, the context patient's known identifiers and the
/// names of the patient, guardian and emergency contact. Each distinct value gets a stable placeholder
/// (<c>[PHONE_1]</c>) so meaning is preserved. Deterministic and offline: names the context does not know (a relative
/// mentioned in passing) cannot be recognised, which is why the context loader supplies every name on file.
/// </summary>
internal static partial class PhiRedactor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> Honorifics = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "miss", "dr", "shri", "sri", "smt", "kumari", "master", "baby", "late", "sr", "jr",
    };

    public static RedactionResult Redact(string text, RedactionContext? context = null) => Redact([text], context);

    /// <summary>
    /// Redacts texts that are sent together, such as a request's system prompt and messages, with one set of
    /// placeholders: <c>[NAME_1]</c> stands for the same value in each, and the counts cover them all.
    /// </summary>
    public static RedactionResult Redact(IReadOnlyList<string> texts, RedactionContext? context = null)
    {
        var state = new State();
        var identifiers = (context?.Identifiers ?? []).Select(i => i.Trim()).Where(i => i.Length >= 4).Distinct().OrderByDescending(i => i.Length).ToList();
        var names = NameVariants(context?.Names ?? []);
        return new RedactionResult([.. texts.Select(text => RedactOne(text, identifiers, names, state))], state.Map, state.Counts);
    }

    private static string RedactOne(string text, IReadOnlyList<string> identifiers, IReadOnlyList<string> names, State state)
    {
        var result = text;
        result = Email().Replace(result, m => state.Placeholder("EMAIL", m.Value));
        result = AbhaAddress().Replace(result, m => state.Placeholder("ABHA", m.Value));

        foreach (var identifier in identifiers)
        {
            result = Regex.Replace(result, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(identifier)}(?![\p{{L}}\p{{N}}])",
                m => state.Placeholder("ID", m.Value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        }

        result = UhidKeyword().Replace(result, m => m.Groups["label"].Value + state.Placeholder("UHID", Compact(m.Groups["id"].Value)));
        result = UhidFormat().Replace(result, m => state.Placeholder("UHID", m.Value.ToUpperInvariant()));
        result = Abha().Replace(result, m => state.Placeholder("ABHA", m.Value));
        result = Aadhaar().Replace(result, m => state.Placeholder("AADHAAR", m.Value));
        result = Mobile().Replace(result, m => state.Placeholder("PHONE", m.Value));
        result = Landline().Replace(result, m => state.Placeholder("PHONE", m.Value));
        result = LongNumber().Replace(result, m => state.Placeholder("ID", m.Value));

        foreach (var name in names)
        {
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){string.Join(@"\s+", name.Split(' ').Select(Regex.Escape))}(?![\p{{L}}\p{{N}}])";
            result = Regex.Replace(result, pattern, m => state.Placeholder("NAME", m.Value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        }

        return result;
    }

    /// <summary>Full names first (longest first), then their individual words, skipping honorifics and single letters.</summary>
    internal static IReadOnlyList<string> NameVariants(IEnumerable<string> names)
    {
        var full = names
            .Select(n => Whitespace().Replace(n.Trim(), " "))
            .Where(n => n.Length >= 2)
            .ToList();
        var words = full.SelectMany(n => n.Split(' '))
            .Select(w => w.Trim('.', ',', '\''))
            .Where(w => w.Length >= 2 && !Honorifics.Contains(w));
        return full.Concat(words)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .ToList();
    }

    private static string Compact(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Za-z0-9._-]+@(?:abdm|sbx|ndhm)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AbhaAddress();

    [GeneratedRegex(@"(?<label>\b(?:u\.?\s?h\.?\s?i\.?\s?d\.?|patient\s+(?:id|number|no\.?)|hospital\s+(?:id|number|no\.?)|registration\s+(?:number|no\.?)|mrn)\s*(?:number|no\.?|#)?\s*[:\-]?\s*)(?<id>[A-Za-z]{0,6}[\s-]?\d(?:[\s-]?\d){1,14})",
        RegexOptions.IgnoreCase)]
    private static partial Regex UhidKeyword();

    [GeneratedRegex(@"\b[A-Za-z]{2,6}\d{8}\b")]
    private static partial Regex UhidFormat();

    [GeneratedRegex(@"(?<!\d)\d{2}[\s-]?\d{4}[\s-]?\d{4}[\s-]?\d{4}(?!\d)")]
    private static partial Regex Abha();

    [GeneratedRegex(@"(?<!\d)\d{4}[\s-]?\d{4}[\s-]?\d{4}(?!\d)")]
    private static partial Regex Aadhaar();

    [GeneratedRegex(@"(?<![\d+])(?:\+?91[\s-]?|0)?[6-9](?:[\s-]?\d){9}(?!\d)")]
    private static partial Regex Mobile();

    [GeneratedRegex(@"(?<!\d)0\d{2,4}[\s-]?\d{6,8}(?!\d)")]
    private static partial Regex Landline();

    [GeneratedRegex(@"(?<!\d)\d{10,}(?!\d)")]
    private static partial Regex LongNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class State
    {
        private readonly Dictionary<string, string> _byValue = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, int> Counts => _counts;

        public string Placeholder(string category, string value)
        {
            var key = category + "|" + Compact(value);
            if (_byValue.TryGetValue(key, out var existing))
            {
                return existing;
            }

            _counts[category] = _counts.GetValueOrDefault(category) + 1;
            var placeholder = string.Create(CultureInfo.InvariantCulture, $"[{category}_{_counts[category]}]");
            _byValue[key] = placeholder;
            Map[placeholder] = value.Trim();
            return placeholder;
        }
    }
}
