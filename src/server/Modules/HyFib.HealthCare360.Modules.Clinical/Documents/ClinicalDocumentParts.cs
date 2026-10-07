using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>
/// The parts and style shared by the clinical documents (prescription and discharge summary), as the staff app printed
/// them: the clinical letterhead with the doctor's block on the right, the patient strip, the allergy line, text
/// sections and the signature block. The server adds the facility logo and the doctor's signature image.
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
    public static Html Letterhead(FacilityLetterhead? letterhead, ImageData? logo, Html right, PrintTemplate? template = null)
    {
        if (template is { PrintsLetterhead: false })
        {
            return Html.Of($"<header class=\"letterhead preprinted\"><div></div>{right}</header>");
        }

        var contact = Join(letterhead?.Phone, letterhead?.Email, letterhead?.Website);
        var registration = Join(
            letterhead?.RegistrationNumber is { Length: > 0 } number ? $"Reg. {number}" : null,
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
    public static Html Prescriber(PrescriberInfo? prescriber)
    {
        if (prescriber is null)
        {
            return Html.Empty;
        }

        // "Cardiology", not "Cardiology, Cardiology" when the specialty and the department share a name.
        var practice = string.Join(", ", new[] { prescriber.Specialty, prescriber.Department }.Where(part => !string.IsNullOrEmpty(part)).Distinct(StringComparer.OrdinalIgnoreCase));
        var registration = prescriber.RegistrationNumber is { Length: > 0 } number
            ? prescriber.RegistrationCouncil is { Length: > 0 } council ? $"Reg. No. {number} ({council})" : $"Reg. No. {number}"
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
    public static Html Field(string label, string? value) =>
        Html.Of($"<div><div class=\"label\">{label}</div><div>{(string.IsNullOrEmpty(value) ? DocumentFormat.Missing : value)}</div></div>");

    /// <summary>Known allergies in red, or a plain "none recorded".</summary>
    public static Html AllergyLine(IReadOnlyList<string> allergies) => allergies.Count > 0
        ? Html.Of($"<div class=\"allergy\">Known allergies: {string.Join(", ", allergies)}</div>")
        : Html.Of($"<div class=\"small\">Known allergies: none recorded</div>");

    /// <summary>A titled block of the doctor's text, line breaks and indents kept; nothing when there is no text.</summary>
    public static Html TextSection(string title, string? text) => string.IsNullOrWhiteSpace(text)
        ? Html.Empty
        : Html.Of($"<div class=\"section\"><h3>{title}</h3><div class=\"pre\">{Html.Lines(text)}</div></div>");

    /// <summary>"Review on 03 Oct 2026." and the follow-up advice; nothing when there is neither.</summary>
    public static Html FollowUp(DateOnly? date, string? advice) => date is null && string.IsNullOrEmpty(advice)
        ? Html.Empty
        : Html.Of($"<div class=\"section\"><h3>Follow-up</h3><div>{(date is { } d ? $"Review on {DocumentFormat.Date(d)}. " : null)}{advice}</div></div>");

    /// <summary>
    /// The signature block: what is printed above it (the time of the digital signature), the doctor's signature image
    /// when the document is signed, and the name on the line.
    /// </summary>
    public static Html Signature(Html above, ImageData? signature, string name) => Html.Of($"""
        <div class="sign">
          {above}
          {Html.Image(signature, $"Signature of {name}", "sign-img")}
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
