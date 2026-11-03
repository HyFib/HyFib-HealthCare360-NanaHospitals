using FluentValidation;
using FluentValidation.Resources;

namespace HyFib.HealthCare360.Platform.Localization;

/// <summary>
/// FluentValidation's own messages ("'Reason' must not be empty.") in the patient languages, worded without the
/// field's name. FluentValidation writes a rule's message in the request's UI culture, which is the patient's language
/// on the portal (<see cref="RequestLanguageMiddleware"/>), but its Hindi and Tamil put the field's English name
/// into the sentence. These come from the patient texts instead (<c>validator.*</c>, keyed by FluentValidation's
/// name for the rule, such as <c>validator.NotEmptyValidator</c>) and are shown next to the field they are about.
/// English keeps FluentValidation's own wording, which staff read. A rule with a message of its own
/// (<c>WithMessage</c>) is translated from its English text like any other server message.
/// </summary>
public sealed class PatientValidatorMessages : LanguageManager
{
    public const string KeyPrefix = "validator.";

    private static readonly Lazy<PatientValidatorMessages> Shared = new(() => new PatientValidatorMessages(PatientTexts.Catalog));

    public PatientValidatorMessages(PatientTexts texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        foreach (var (language, entries) in texts.Languages)
        {
            if (language == PatientLanguages.English)
            {
                continue;
            }

            // The middleware sets the culture with its region ("hi-IN"); FluentValidation looks for that, then the language.
            var cultures = new[] { language, PatientLanguages.CultureOf(language).Name }.Distinct(StringComparer.Ordinal).ToList();
            foreach (var (key, text) in entries.Where(e => e.Key.StartsWith(KeyPrefix, StringComparison.Ordinal)))
            {
                foreach (var culture in cultures)
                {
                    AddTranslation(culture, key[KeyPrefix.Length..], text);
                }
            }
        }
    }

    /// <summary>Makes these FluentValidation's messages for the whole server (a static setting of FluentValidation).</summary>
    public static void Install() => ValidatorOptions.Global.LanguageManager = Shared.Value;
}
