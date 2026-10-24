using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Localization;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>
/// The labels of a clinical document (PP-06): in one language (the discharge summary, in the patient's language), or in
/// English with a second language after it (the prescription, ERX-04: the patient's Hindi or Tamil, else Hindi).
/// </summary>
internal sealed record ClinicalLabels(DocumentText Text, DocumentText? Second)
{
    public static ClinicalLabels English { get; } = new(DocumentText.English, null);

    /// <summary>English and Hindi.</summary>
    public static ClinicalLabels EnglishAndHindi { get; } = new(DocumentText.English, DocumentText.Hindi);

    /// <summary>English and Tamil.</summary>
    public static ClinicalLabels EnglishAndTamil { get; } = new(DocumentText.English, DocumentText.Tamil);

    public static ClinicalLabels For(DocumentText text) => text.IsEnglish ? English : new(text, null);

    /// <summary>English with the patient's own language after it when they read Hindi or Tamil, else with Hindi.</summary>
    public static ClinicalLabels EnglishAndPatients(string? preferredLanguage) =>
        PatientLanguages.FromPreference(preferredLanguage) == PatientLanguages.Tamil ? EnglishAndTamil : EnglishAndHindi;

    public bool Bilingual => Second is not null;

    /// <summary>The language of the page: English for a bilingual document, whose second language is marked where it is.</summary>
    public string Language => Bilingual ? DocumentText.English.Language : Text.Language;

    public Html this[string key] => Format(key);

    public Html Format(string key, params ReadOnlySpan<(string Name, string? Value)> values) =>
        Second is { } second ? DocumentText.Bilingual(second, key, values) : Html.Text(Text.Format(key, values));

    /// <summary>Plain text for where markup cannot go (an image's alternative text): the first language only.</summary>
    public string Plain(string key, params ReadOnlySpan<(string Name, string? Value)> values) => Text.Format(key, values);
}

/// <summary>
/// The parts and style shared by the clinical documents (prescription and discharge summary), as the staff app printed
/// them: the clinical letterhead with the doctor's block on the right, the patient strip, the allergy line, text
/// sections and the signature block. The server adds the facility logo and the doctor's signature image. Their words
/// come from <see cref="ClinicalLabels"/> (English unless given).
/// </summary>
internal static class ClinicalDocumentParts
{
    /// <summary>Printed when the facility could not be found (it always can: facilities are never deleted).</summary>
    public const string FallbackFacilityName = "Hospital";

    public const string Styles = """
        .letterhead { display: flex; justify-content: space-between; gap: 12px; border-bottom: 2px solid #045E80; padding-bottom: 6px; margin-bottom: 8px; }
        .letterhead .brand { display: flex; gap: 10px; align-items: center; }
        .letterhead .logo { max-height: 16mm; max-width: 36mm; object-fit: contain; }
        .letterhead.preprinted { border-bottom: none; padding-bottom: 0; }
        .facility { font-size: 14pt; font-weight: 700; color: #045E80; }
        .doctor { text-align: right; }
        .doctor .name { font-weight: 700; font-size: 11pt; }
        .patient { display: grid; grid-template-columns: repeat(4, auto); gap: 2px 14px; padding: 6px 8px; background: #F2F7FA; border-radius: 4px; margin-bottom: 6px; }
        .label { color: #555; font-size: 8pt; text-transform: uppercase; letter-spacing: .4px; }
        .allergy { color: #B00020; font-weight: 700; }
        .section { margin-top: 8px; }
        .section h3 { font-size: 10pt; color: #045E80; margin: 0 0 2px; }
        .pre { white-space: pre-wrap; }
        .footer { display: flex; justify-content: space-between; align-items: flex-end; margin-top: 14px; gap: 12px; break-inside: avoid; }
        .sign { text-align: right; min-width: 50mm; }
        .sign .sign-img { display: block; margin: 4px 0 0 auto; max-height: 14mm; max-width: 45mm; }
        .sign .line { border-top: 1px solid #333; margin-top: 18px; padding-top: 2px; }
        .sign .sign-img + .line { margin-top: 2px; }
        .draft { color: #B00020; font-weight: 700; border: 2px solid #B00020; padding: 2px 6px; display: inline-block; }

        """;

    /// <summary>
    /// The clinical letterhead: logo, facility name, address, "phone · e-mail · website", "Reg. … · NABH …" and the
    /// facility's own letterhead line, with <paramref name="right"/> (the doctor's block) on the right. On pre-printed
    /// stationery (<paramref name="template"/>) only the doctor's block is printed: the paper has the rest.
    /// </summary>
    public static Html Letterhead(FacilityLetterhead? letterhead, ImageData? logo, Html right, PrintTemplate? template = null, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        if (template is { PrintsLetterhead: false })
        {
            return Html.Of($"<header class=\"letterhead preprinted\"><div></div>{right}</header>");
        }

        var contact = Join(letterhead?.Phone, letterhead?.Email, letterhead?.Website);
        var registration = Join(
            letterhead?.RegistrationNumber is { Length: > 0 } number ? text.Format("doc.clinical.facility_registration", ("number", number)) : null,
            letterhead?.NabhNumber is { Length: > 0 } nabh ? $"NABH {nabh}" : null);
        return Html.Of($"""
            <header class="letterhead">
              <div class="brand">
                {Html.Image(logo, "", "logo")}
                <div>
                  <div class="facility">{letterhead?.Name ?? FallbackFacilityName}</div>
                  {Small(letterhead?.Address)}
                  {Small(contact)}
                  {Small(registration)}
                  {Small(letterhead?.LetterheadNote)}
                </div>
              </div>
              {right}
            </header>
            """);
    }

    /// <summary>The doctor's block required on Indian prescriptions: name, qualification, specialty and council registration.</summary>
    public static Html Prescriber(PrescriberInfo? prescriber, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        if (prescriber is null)
        {
            return Html.Empty;
        }

        // "Cardiology", not "Cardiology, Cardiology" when the specialty and the department share a name.
        var practice = string.Join(", ", new[] { prescriber.Specialty, prescriber.Department }.Where(part => !string.IsNullOrEmpty(part)).Distinct(StringComparer.OrdinalIgnoreCase));
        var registration = prescriber.RegistrationNumber is { Length: > 0 } number
            ? prescriber.RegistrationCouncil is { Length: > 0 } council
                ? text.Format("doc.clinical.doctor_registration_council", ("number", number), ("council", council))
                : text.Format("doc.clinical.doctor_registration", ("number", number))
            : null;
        return Html.Of($"""
            <div class="doctor">
              <div class="name">{prescriber.DisplayName}</div>
              {Small(prescriber.Qualification)}
              {Small(practice)}
              {Small(registration)}
            </div>
            """);
    }

    /// <summary>One labelled value of the patient strip; a missing value prints as a dash.</summary>
    public static Html Field(string label, string? value) => Field(Html.Text(label), value);

    public static Html Field(Html label, string? value) =>
        Html.Of($"<div><div class=\"label\">{label}</div><div>{(string.IsNullOrEmpty(value) ? DocumentFormat.Missing : value)}</div></div>");

    /// <summary>Known allergies in red, or a plain "none recorded".</summary>
    public static Html AllergyLine(IReadOnlyList<string> allergies, ClinicalLabels? labels = null)
    {
        labels ??= ClinicalLabels.English;
        return allergies.Count > 0
            ? Html.Of($"<div class=\"allergy\">{labels["doc.clinical.known_allergies"]}: {string.Join(", ", allergies)}</div>")
            : Html.Of($"<div class=\"small\">{labels["doc.clinical.known_allergies"]}: {labels["doc.clinical.none_recorded"]}</div>");
    }

    /// <summary>A titled block of the doctor's text, line breaks and indents kept; nothing when there is no text.</summary>
    public static Html TextSection(string title, string? text) => TextSection(Html.Text(title), text);

    public static Html TextSection(Html title, string? text) => string.IsNullOrWhiteSpace(text)
        ? Html.Empty
        : Html.Of($"<div class=\"section\"><h3>{title}</h3><div class=\"pre\">{Html.Lines(text)}</div></div>");

    /// <summary>"Review on 03 Oct 2026." and the follow-up advice; nothing when there is neither.</summary>
    public static Html FollowUp(DateOnly? date, string? advice, ClinicalLabels? labels = null)
    {
        labels ??= ClinicalLabels.English;
        return date is null && string.IsNullOrEmpty(advice)
            ? Html.Empty
            : Html.Of($"<div class=\"section\"><h3>{labels["doc.clinical.follow_up"]}</h3><div>{(date is { } d ? Html.Of($"{labels.Format("doc.clinical.review_on", ("date", DocumentFormat.Date(d)))} ") : Html.Empty)}{advice}</div></div>");
    }

    /// <summary>
    /// The signature block: what is printed above it (the time of the digital signature), the doctor's signature image
    /// when the document is signed, and the name on the line.
    /// </summary>
    public static Html Signature(Html above, ImageData? signature, string name, ClinicalLabels? labels = null) => Html.Of($"""
        <div class="sign">
          {above}
          {Html.Image(signature, (labels ?? ClinicalLabels.English).Plain("doc.clinical.signature_of", ("name", name)), "sign-img")}
          <div class="line">{name}</div>
        </div>
        """);

    public static string GenderShort(Gender gender) => gender switch
    {
        Gender.Male => "M",
        Gender.Female => "F",
        Gender.Other => "O",
        _ => DocumentFormat.Missing,
    };

    private static Html Small(string? text) => string.IsNullOrWhiteSpace(text) ? Html.Empty : Html.Of($"<div class=\"small\">{text}</div>");

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(part => !string.IsNullOrEmpty(part)));
}
