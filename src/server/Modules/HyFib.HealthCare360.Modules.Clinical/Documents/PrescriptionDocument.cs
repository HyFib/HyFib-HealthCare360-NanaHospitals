using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>
/// The A5 e-prescription (ERX-04, PP-02): clinical letterhead with the facility logo and the prescriber's block, the
/// patient strip, known allergies, diagnosis, ℞ with each medicine's directions in English and the line in the
/// patient's language under them, Schedule H1/X tags, advice, follow-up, the QR code that verifies the prescription, and the signature with the
/// prescriber's signature image: "Digitally signed by …" with the certificate when the doctor signed it with their DSC,
/// else "Signed electronically on …" (signed in HC360, which is not a digital signature under the IT Act). The same
/// document is printed at the counter, downloaded from the portal and sent with the "prescription shared" message. It
/// is bilingual whoever reads it (ERX-04): every label is printed in English with the same in the patient's language
/// after it, Tamil for a patient whose record says Tamil and Hindi for everyone else, and the page carries that script's
/// font. Medicine names, doses and codes are not translated.
/// </summary>
internal static class PrescriptionDocument
{
    private const string Styles = """
        .rx { font-size: 20pt; font-weight: 700; font-family: "Times New Roman", serif; margin: 4px 0; }
        table.lines th { border-bottom: 1px solid #999; font-size: 8.5pt; color: #333; }
        table.lines td { border-bottom: 1px solid #E3E3E3; }
        .drug { font-weight: 600; }
        .hindi, .tamil { color: #333; font-size: 9pt; }
        .hi, .ta { color: #333; }
        .controlled { display: inline-block; border: 1px solid #B00020; color: #B00020; font-size: 7.5pt; padding: 0 4px; border-radius: 3px; margin-left: 4px; }
        .qr .qr-img { display: block; width: 26mm; height: 26mm; }
        .qr .mono { word-break: break-all; }
        .dsc { max-width: 62mm; }
        .dsc.mono { word-break: break-all; font-size: 7pt; }

        """;

    /// <summary>
    /// The document as the facility's prescriptions template says (HMS-04: A5 or A4, letterhead or pre-printed paper,
    /// logo, closing note), with the facility logo and, once signed, the prescriber's signature image.
    /// </summary>
    public static async Task<PrintableDocument> BuildAsync(PrescriptionPrintModel model, DocumentAssets assets, CancellationToken cancellationToken)
    {
        var template = await assets.TemplateAsync(PrintTemplateCatalog.Prescription, model.Letterhead?.FacilityId, cancellationToken);
        var logo = template.ShowLogo ? await assets.ImageAsync(model.Letterhead?.LogoFileId, cancellationToken) : null;
        var signature = model.SignedAt is null ? null : await assets.ImageAsync(model.Prescriber?.SignatureFileId, cancellationToken);
        return Build(model, logo, signature, template);
    }

    public static PrintableDocument Build(PrescriptionPrintModel model, ImageData? logo, ImageData? signature, PrintTemplate? template = null) => new()
    {
        Title = $"Prescription {model.PrescriptionNumber}",
        FileName = $"prescription-{model.PrescriptionNumber}",
        PageSize = template?.Paper ?? DocumentPageSize.A5,
        Footer = Footer(model),
        Styles = ClinicalDocumentParts.Styles + Styles,
        Template = template,
        Body = Body(model, logo, signature, template),
    };

    /// <summary>"Asha Rao · UHID NH-000123 · Prescription NHRX260000123", and the warning on a copy that is not valid.</summary>
    public static string Footer(PrescriptionPrintModel model) =>
        string.Join(" · ", new[] { model.Patient.Name, $"UHID {model.Patient.Uhid}", $"Prescription {model.PrescriptionNumber}", StatusWarning(model.Status) }
            .Where(part => part is not null));

    public static Html Body(PrescriptionPrintModel model, ImageData? logo, ImageData? signature, PrintTemplate? template = null)
    {
        var labels = ClinicalLabels.EnglishAndPatients(model.Patient.PreferredLanguage);
        var second = labels.Second ?? DocumentText.Hindi;
        var patient = model.Patient;
        var warning = StatusWarningKey(model.Status) is { } key ? Html.Of($"<div class=\"draft\">{labels[key]}</div>") : Html.Empty;
        var diagnosis = model.Diagnoses.Count > 0
            ? Html.Of($"<div><span class=\"label\">{labels["doc.clinical.diagnosis"]}</span> {string.Join("; ", model.Diagnoses)}</div>")
            : Html.Empty;
        var refills = model.RefillsAllowed > 0
            ? Html.Of($"<div class=\"small\">{labels.Format("doc.rx.refills_allowed", ("count", model.RefillsAllowed.ToString(Html.Culture)))}</div>")
            : Html.Empty;
        var signed = SignatureLine(model, labels);
        return Html.Of($"""
            {ClinicalDocumentParts.Letterhead(model.Letterhead, logo, ClinicalDocumentParts.Prescriber(model.Prescriber), template)}
            {warning}
            <div class="patient">
              {ClinicalDocumentParts.Field(labels["doc.patient"], patient.Name)}
              {ClinicalDocumentParts.Field("UHID", patient.Uhid)}
              {ClinicalDocumentParts.Field(labels["doc.age_sex"], $"{patient.Age ?? DocumentFormat.Missing} / {ClinicalDocumentParts.GenderShort(patient.Gender)}")}
              {ClinicalDocumentParts.Field(labels["doc.date"], model.SignedAt is { } date ? DocumentFormat.DateTime(date) : null)}
            </div>
            {ClinicalDocumentParts.AllergyLine(model.KnownAllergies, labels)}
            {diagnosis}
            <div class="rx keep-next">℞</div>
            <table class="lines">
              <thead><tr><th></th><th>{labels["doc.rx.medicine"]}</th><th>{labels["doc.rx.dose_directions"]}</th><th>{labels["doc.rx.duration"]}</th><th class="right">{labels["doc.rx.quantity"]}</th></tr></thead>
              <tbody>{model.Lines.Select(line => Line(line, second))}</tbody>
            </table>
            {ClinicalDocumentParts.TextSection(labels["doc.clinical.advice"], model.Advice)}
            {ClinicalDocumentParts.FollowUp(model.FollowUpDate, model.FollowUpAdvice, labels)}
            {refills}
            <div class="footer">
              {Verification(model, labels)}
              {ClinicalDocumentParts.Signature(signed, signature, model.Prescriber?.DisplayName ?? "Doctor", labels)}
            </div>
            """);
    }

    /// <summary>
    /// "Digitally signed by Dr Meera Shah (MMC-55821) — certificate 1A2B3C, 25 Sep 2026, 3:45 pm" and the SHA-256 of the
    /// signed bytes (the verification page shows it too) for a DSC signature; "Signed electronically on …" otherwise.
    /// </summary>
    public static Html SignatureLine(PrescriptionPrintModel model, ClinicalLabels? labels = null)
    {
        labels ??= ClinicalLabels.EnglishAndPatients(model.Patient.PreferredLanguage);
        if (model.DigitalSignature is { } dsc)
        {
            var signer = dsc.RegistrationNumber is { Length: > 0 } registration ? $"{dsc.SignerName} ({registration})" : dsc.SignerName;
            var line = labels.Format("doc.rx.dsc_signed_by", ("signer", signer), ("serial", dsc.SerialNumber), ("at", DocumentFormat.DateTime(dsc.SignedAt)));
            return Html.Of($"""
                <div class="small dsc">{line}</div>
                <div class="small dsc mono">SHA-256 {dsc.ContentHash}</div>
                """);
        }

        return model.SignedAt is { } at ? Html.Of($"<div class=\"small\">{labels.Format("doc.rx.signed_on", ("at", DocumentFormat.DateTime(at)))}</div>") : Html.Empty;
    }

    /// <summary>"5 days", "1 day", and whole weeks from two weeks on ("3 weeks"), as the staff app writes durations.</summary>
    public static string DurationText(int days, DocumentText? text = null)
    {
        text ??= DocumentText.English;
        return days <= 0 ? ""
            : days >= 14 && days % 7 == 0 ? text.Format("doc.rx.weeks", ("count", (days / 7).ToString(Html.Culture)))
            : days == 1 ? text["doc.rx.one_day"]
            : text.Format("doc.rx.days", ("count", days.ToString(Html.Culture)));
    }

    /// <summary>The warning across a copy that must not be dispensed (in English, for the page footer); none on a signed prescription.</summary>
    public static string? StatusWarning(PrescriptionStatus status) => StatusWarningKey(status) is { } key ? DocumentText.English[key] : null;

    private static string? StatusWarningKey(PrescriptionStatus status) => status switch
    {
        PrescriptionStatus.Signed => null,
        PrescriptionStatus.Cancelled => "doc.rx.cancelled",
        _ => "doc.rx.draft",
    };

    // One medicine: name, strength and Schedule H1/X tag; generic and form; the dose with English directions and the
    // route (unless oral), the same in the patient's language, and the instructions; duration and quantity.
    private static Html Line(PrescriptionPrintLine line, DocumentText second)
    {
        var name = line.Strength is { Length: > 0 } strength ? $"{line.DrugName} {strength}" : line.DrugName;
        var controlled = line.IsControlledDrug ? Html.Of($"<span class=\"controlled\">Schedule H1/X</span>") : Html.Empty;
        var generic = line.GenericName is { Length: > 0 } genericName
            ? Html.Of($"<div class=\"small\">{(line.DosageForm is { Length: > 0 } form ? $"{genericName} · {form}" : genericName)}</div>")
            : Html.Empty;
        var route = line.Route is { Length: > 0 } via && via != "Oral" ? $" · {via}" : null;
        var instructions = line.Instructions is { Length: > 0 } text ? Html.Of($"<div class=\"small\">{text}</div>") : Html.Empty;
        return Html.Of($"""
            <tr>
              <td>{line.Sequence}.</td>
              <td>
                <div class="drug">{name}{controlled}</div>
                {generic}
              </td>
              <td>
                <div>{line.Dose} · {line.FrequencyEnglish}{route}</div>
                <div class="{SecondClass(second)}" lang="{second.Language}">{line.Dose} · {(second == DocumentText.Tamil ? line.FrequencyTamil : line.FrequencyHindi)}</div>
                {instructions}
              </td>
              <td>{Duration(line, second)}</td>
              <td class="right">{line.Quantity:0.##}</td>
            </tr>
            """);
    }

    // "30 days" with "30 दिन" (or "30 நாட்கள்") under it; a STAT dose is given once.
    private static Html Duration(PrescriptionPrintLine line, DocumentText second)
    {
        var (english, translated) = line.Frequency == "STAT"
            ? (DocumentText.English["doc.rx.once"], second["doc.rx.once"])
            : (DurationText(line.DurationDays, DocumentText.English), DurationText(line.DurationDays, second));
        return english.Length == 0
            ? Html.Empty
            : Html.Of($"{english}<div class=\"{SecondClass(second)}\" lang=\"{second.Language}\">{translated}</div>");
    }

    // The class of a line in the second language, for its smaller grey type.
    private static string SecondClass(DocumentText second) => second == DocumentText.Tamil ? "tamil" : "hindi";

    // The QR code opens the portal's public verification page (prescriber, facility, date, still valid or not; no
    // patient data). Without the hospital's public address (portal.baseUrl) the code is printed without a QR code, as
    // on the reports.
    private static Html Verification(PrescriptionPrintModel model, ClinicalLabels labels)
    {
        var check = model.VerificationUrl is { } url
            ? Html.Image(DocumentAssets.QrCode(url), labels.Plain("doc.rx.qr_alt"), "qr-img")
            : Html.Empty;
        var caption = model.VerificationUrl is not null
            ? Html.Of($"<div class=\"small\">{labels["doc.rx.scan_to_verify"]}</div>")
            : model.VerificationToken is { } token
                ? Html.Of($"<div class=\"small\">{labels["doc.verify.code_label"]} <span class=\"mono\">{token}</span></div>")
                : Html.Empty;
        return Html.Of($"""
            <div class="qr">
              {check}
              <div class="small">{model.PrescriptionNumber}</div>
              {caption}
            </div>
            """);
    }
}
