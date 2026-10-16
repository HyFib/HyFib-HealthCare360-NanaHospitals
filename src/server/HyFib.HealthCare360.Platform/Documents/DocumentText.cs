using HyFib.HealthCare360.Platform.Localization;

namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>
/// The labels of a document in one language (SRS PP-06): <c>text["doc.patient"]</c>. A document for a patient is made in
/// the patient's language (their record's preference, or the portal's language); the staff copy in English. Only the
/// document's own words change: names, medicines, doses, codes, numbers, dates and amounts print as they are.
/// </summary>
public sealed class DocumentText
{
    private static readonly IReadOnlyDictionary<string, DocumentText> ByLanguage =
        PatientLanguages.Codes.ToDictionary(code => code, code => new DocumentText(code), StringComparer.Ordinal);

    private DocumentText(string language) => Language = language;

    public static DocumentText English => ByLanguage[PatientLanguages.English];

    public static DocumentText Hindi => ByLanguage[PatientLanguages.Hindi];

    /// <summary>The labels in <paramref name="language"/>, or in English when HC360 does not write that language.</summary>
    public static DocumentText For(string? language) => ByLanguage[PatientLanguages.OrDefault(language)];

    /// <summary>The language code, for the document's <c>lang</c> attribute.</summary>
    public string Language { get; }

    public bool IsEnglish => Language == PatientLanguages.English;

    public string this[string key] => PatientTexts.Catalog.Text(Language, key);

    /// <summary>A label with its placeholders filled in: <c>text.Format("doc.bill.payable_by", ("payer", name))</c>.</summary>
    public string Format(string key, params ReadOnlySpan<(string Name, string? Value)> values) => PatientTexts.Catalog.Text(Language, key, values);

    /// <summary>
    /// A label in English followed by its Hindi, for the documents the SRS asks to be bilingual (the prescription,
    /// ERX-04): "Medicine / दवा".
    /// </summary>
    public static Html Bilingual(string key, params ReadOnlySpan<(string Name, string? Value)> values) => Html.Of(
        $"{English.Format(key, values)} <span class=\"hi\" lang=\"hi\">/ {Hindi.Format(key, values)}</span>");
}
