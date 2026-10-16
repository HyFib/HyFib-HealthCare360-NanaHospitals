using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace HyFib.HealthCare360.Platform.Localization;

/// <summary>A language patients can read HC360's own text in: its code, and the names a patient record may give it.</summary>
/// <param name="Code">The ISO 639-1 code ("hi"), as in <c>lang</c> attributes and Accept-Language.</param>
/// <param name="Culture">The culture its text is written for ("hi-IN").</param>
/// <param name="Names">What the free-text "preferred language" on a patient record may say, in lower case.</param>
public sealed record PatientLanguage(string Code, string Culture, IReadOnlyList<string> Names);

/// <summary>
/// The languages of the text HC360 writes for patients (SRS PP-06): server messages on the portal and the documents
/// patients receive. English is the default and always complete. Another language is added by adding it here and its
/// text file next to <c>patient-texts-en.json</c> (<see cref="PatientTexts"/>); the tests then require every text in it.
/// </summary>
public static class PatientLanguages
{
    public const string English = "en";
    public const string Hindi = "hi";
    public const string Default = English;

    public static IReadOnlyList<PatientLanguage> All { get; } =
    [
        new(English, "en-IN", ["english", "eng", "अंग्रेज़ी", "अंग्रेजी"]),
        new(Hindi, "hi-IN", ["hindi", "hin", "हिन्दी", "हिंदी"]),
    ];

    public static IReadOnlyList<string> Codes { get; } = [.. All.Select(l => l.Code)];

    public static bool IsSupported(string? code) => code is not null && All.Any(l => l.Code == code);

    /// <summary>The code when it is supported, otherwise English.</summary>
    public static string OrDefault(string? code) => IsSupported(code) ? code! : Default;

    /// <summary>The culture to write a language's text in ("hi" → hi-IN); English (en-IN) for anything else.</summary>
    public static CultureInfo CultureOf(string? code) =>
        CultureInfo.GetCultureInfo(All.FirstOrDefault(l => l.Code == code)?.Culture ?? All[0].Culture);

    /// <summary>
    /// The language a patient record's free-text preference names: a code or culture ("hi", "hi-IN"), or a name in
    /// English or in the language itself ("Hindi", "हिन्दी"). Null when it names none HC360 writes (blank, "Marathi").
    /// </summary>
    public static string? FromPreference(string? preference)
    {
        var text = preference?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var language in All)
        {
            if (text == language.Code || text.StartsWith(language.Code + "-", StringComparison.Ordinal) || text.StartsWith(language.Code + "_", StringComparison.Ordinal)
                || language.Names.Any(name => text == name || text.StartsWith(name + " ", StringComparison.Ordinal) || text.StartsWith(name + "(", StringComparison.Ordinal)))
            {
                return language.Code;
            }
        }

        return null;
    }

    /// <summary>
    /// The supported language the Accept-Language header prefers most ("hi-IN,hi;q=0.9,en;q=0.8" → hi), or null when it
    /// names none (or is missing or malformed). A wildcard is not a choice.
    /// </summary>
    public static string? FromAcceptLanguage(IList<string>? header)
    {
        if (header is null || header.Count == 0 || !StringWithQualityHeaderValue.TryParseList(header, out var values))
        {
            return null;
        }

        string? best = null;
        var bestQuality = 0.0;
        foreach (var value in values)
        {
            var quality = value.Quality ?? 1.0;
            var tag = value.Value.Value?.Trim().ToLowerInvariant();
            if (quality <= bestQuality || string.IsNullOrEmpty(tag) || tag == "*")
            {
                continue;
            }

            var primary = tag.Split('-', 2)[0];
            if (IsSupported(primary))
            {
                best = primary;
                bestQuality = quality;
            }
        }

        return best;
    }
}
