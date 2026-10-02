using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>
/// The A5 e-prescription (ERX-04, PP-02): clinical letterhead with the facility logo and the prescriber's block, the
/// patient strip, known allergies, diagnosis, ℞ with each medicine's directions in English and the Hindi line under
/// them, Schedule H1/X tags, advice, follow-up, the QR code that verifies the prescription, and the digital signature
/// with the prescriber's signature image. The same document is printed at the counter, downloaded from the portal and
/// sent with the "prescription shared" message.
/// </summary>
internal static class PrescriptionDocument
{
    private const string Styles = """
        .rx { font-size: 20pt; font-weight: 700; font-family: "Times New Roman", serif; margin: 4px 0; }
        table.lines th { border-bottom: 1px solid #999; font-size: 8.5pt; color: #333; }
        table.lines td { border-bottom: 1px solid #E3E3E3; }
        .drug { font-weight: 600; }
        .hindi { color: #333; font-size: 9pt; }
        .controlled { display: inline-block; border: 1px solid #B00020; color: #B00020; font-size: 7.5pt; padding: 0 4px; border-radius: 3px; margin-left: 4px; }
        .qr .qr-img { display: block; width: 26mm; height: 26mm; }
        .qr .mono { word-break: break-all; }

        """;

    /// <summary>The document with the facility logo and, once signed, the prescriber's signature image.</summary>
    public static async Task<PrintableDocument> BuildAsync(PrescriptionPrintModel model, DocumentAssets assets, CancellationToken cancellationToken)
    {
        var logo = await assets.ImageAsync(model.Letterhead?.LogoFileId, cancellationToken);
        var signature = model.SignedAt is null ? null : await assets.ImageAsync(model.Prescriber?.SignatureFileId, cancellationToken);
        return Build(model, logo, signature);
    }

    public static PrintableDocument Build(PrescriptionPrintModel model, ImageData? logo, ImageData? signature) => new()
    {
        Title = $"Prescription {model.PrescriptionNumber}",
        FileName = $"prescription-{model.PrescriptionNumber}",
        PageSize = DocumentPageSize.A5,
        Footer = Footer(model),
        Styles = ClinicalDocumentParts.Styles + Styles,
        Body = Body(model, logo, signature),
    };

    /// <summary>"Asha Rao · UHID NH-000123 · Prescription NHRX260000123", and the warning on a copy that is not valid.</summary>
    public static string Footer(PrescriptionPrintModel model) =>
        string.Join(" · ", new[] { model.Patient.Name, $"UHID {model.Patient.Uhid}", $"Prescription {model.PrescriptionNumber}", StatusWarning(model.Status) }
            .Where(part => part is not null));

    public static Html Body(PrescriptionPrintModel model, ImageData? logo, ImageData? signature)
    {
        var patient = model.Patient;
        var warning = StatusWarning(model.Status) is { } text ? Html.Of($"<div class=\"draft\">{text}</div>") : Html.Empty;
        var diagnosis = model.Diagnoses.Count > 0
            ? Html.Of($"<div><span class=\"label\">Diagnosis</span> {string.Join("; ", model.Diagnoses)}</div>")
            : Html.Empty;
        var refills = model.RefillsAllowed > 0 ? Html.Of($"<div class=\"small\">Refills allowed: {model.RefillsAllowed}</div>") : Html.Empty;
        var signed = model.SignedAt is { } at ? Html.Of($"<div class=\"small\">Digitally signed on {DocumentFormat.DateTime(at)}</div>") : Html.Empty;
        return Html.Of($"""
            {ClinicalDocumentParts.Letterhead(model.Letterhead, logo, ClinicalDocumentParts.Prescriber(model.Prescriber))}
            {warning}
            <div class="patient">
              {ClinicalDocumentParts.Field("Patient", patient.Name)}
              {ClinicalDocumentParts.Field("UHID", patient.Uhid)}
              {ClinicalDocumentParts.Field("Age / sex", $"{patient.Age ?? DocumentFormat.Missing} / {ClinicalDocumentParts.GenderShort(patient.Gender)}")}
              {ClinicalDocumentParts.Field("Date", model.SignedAt is { } date ? DocumentFormat.DateTime(date) : null)}
            </div>
            {ClinicalDocumentParts.AllergyLine(model.KnownAllergies)}
            {diagnosis}
            <div class="rx keep-next">℞</div>
            <table class="lines">
              <thead><tr><th></th><th>Medicine</th><th>Dose and directions</th><th>Duration</th><th class="right">Qty</th></tr></thead>
              <tbody>{model.Lines.Select(Line)}</tbody>
            </table>
            {ClinicalDocumentParts.TextSection("Advice", model.Advice)}
            {ClinicalDocumentParts.FollowUp(model.FollowUpDate, model.FollowUpAdvice)}
            {refills}
            <div class="footer">
              {Verification(model)}
              {ClinicalDocumentParts.Signature(signed, signature, model.Prescriber?.DisplayName ?? "Doctor")}
            </div>
            """);
    }

    /// <summary>"5 days", "1 day", and whole weeks from two weeks on ("3 weeks"), as the staff app writes durations.</summary>
    public static string DurationText(int days) =>
        days <= 0 ? "" : days >= 14 && days % 7 == 0 ? $"{days / 7} weeks" : days == 1 ? "1 day" : $"{days} days";

    /// <summary>The warning across a copy that must not be dispensed; none on a signed prescription.</summary>
    public static string? StatusWarning(PrescriptionStatus status) => status switch
    {
        PrescriptionStatus.Signed => null,
        PrescriptionStatus.Cancelled => "CANCELLED — do not dispense",
        _ => "DRAFT — not valid for dispensing",
    };

    // One medicine: name, strength and Schedule H1/X tag; generic and form; the dose with English directions and the
    // route (unless oral), the same in Hindi for the patient, and the instructions; duration and quantity.
    private static Html Line(PrescriptionPrintLine line)
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
                <div class="hindi" lang="hi">{line.Dose} · {line.FrequencyHindi}</div>
                {instructions}
              </td>
              <td>{(line.Frequency == "STAT" ? "Once" : DurationText(line.DurationDays))}</td>
              <td class="right">{line.Quantity:0.##}</td>
            </tr>
            """);
    }

    // The QR code opens the portal's public verification page (prescriber, facility, date, still valid or not; no
    // patient data). Without the hospital's public address (portal.baseUrl) the code is printed without a QR code, as
    // on the reports.
    private static Html Verification(PrescriptionPrintModel model)
    {
        var check = model.VerificationUrl is { } url
            ? Html.Image(DocumentAssets.QrCode(url), "QR code to verify this prescription", "qr-img")
            : Html.Empty;
        var caption = model.VerificationUrl is not null
            ? Html.Of($"<div class=\"small\">Scan to verify this prescription</div>")
            : model.VerificationToken is { } token
                ? Html.Of($"<div class=\"small\">Verification code <span class=\"mono\">{token}</span></div>")
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
