using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emergency;

/// <summary>
/// The medico-legal documents made from the MLC record: the wound certificate the examining doctor signed, and the
/// medico-legal report a court (or the police, on requisition) is sent. Both are printed from what HC360 keeps, never
/// typed again, so the court reads what the record says: a draft certificate prints as a draft, and the report leaves it
/// out.
/// </summary>
internal static class MedicoLegalDocuments
{
    public static PrintableDocument WoundCertificate(MedicoLegalRecord r, WoundCertificate w, DocumentLetterhead letterhead, ImageData? signature)
    {
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("Wound certificate")}
            {DocumentParts.Banner(w.IsSigned ? null : "Draft: not signed by the examining doctor. Not to be issued.")}
            {DocumentParts.Fields([
                ("MLC no.", r.Entry.Number),
                ("Patient", r.Patient.FullName),
                ("UHID", r.Patient.Uhid),
                ("Sex", r.Patient.Gender.ToString()),
                ("Age", Age(r)),
                ("Brought by", BroughtBy(r.Entry)),
                ("Police station", r.Entry.PoliceStation),
                ("Examined", DocumentFormat.DateTime(w.ExaminedAt)),
                ("Marks of identification", w.IdentificationMarks),
                ("Alleged history", w.History),
            ])}
            {Injuries(w)}
            {(w.Remarks is { } remarks ? Html.Of($"<p><strong>Remarks.</strong> {remarks}</p>") : Html.Empty)}
            <p>I certify that I examined the person named above on the date and at the time stated, and that the injuries found are as described.</p>
            {Signatory(r, w, signature)}
            """);
        return new PrintableDocument
        {
            Title = $"Wound certificate, MLC {r.Entry.Number}",
            FileName = $"wound-certificate-{r.Entry.Number}",
            Body = body,
            Styles = DocumentParts.Styles + Styles,
            Footer = $"{r.Entry.Number} · Medico-legal: confidential",
            Template = letterhead.Template,
        };
    }

    /// <summary>
    /// The medico-legal report for a court, from the MLC record: the case as registered, the police intimation, the examination
    /// and injuries of the signed wound certificate, a sexual-assault victim's examination (BNSS 2023, s.184), the treatment's
    /// outcome in the emergency department, and the doctor's declaration.
    /// </summary>
    public static PrintableDocument CourtReport(MedicoLegalRecord r, CourtReportRequest request, DocumentLetterhead letterhead, ImageData? signature, DateTimeOffset now)
    {
        var m = r.Entry;
        var c = r.Case;
        var signed = r.Wound is { IsSigned: true } wound ? wound : null;
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("Medico-legal report")}
            <p>To {request.Court.Trim()}{(request.CaseReference is { Length: > 0 } reference ? $", in {reference.Trim()}" : string.Empty)}.</p>
            {(request.Requisition is { Length: > 0 } requisition ? Html.Of($"<p>In reply to {requisition.Trim()}.</p>") : Html.Empty)}
            {DocumentParts.Fields([
                ("MLC no.", m.Number),
                ("Registered", DocumentFormat.DateTime(m.IdentifiedAt)),
                ("Patient", r.Patient.FullName),
                ("UHID", r.Patient.Uhid),
                ("Sex", r.Patient.Gender.ToString()),
                ("Age", Age(r)),
                ("Arrived", DocumentFormat.DateTime(c.ArrivedAt)),
                ("Brought by", BroughtBy(m)),
                ("Alleged cause", m.Cause.ToString()),
                ("Incident", string.Join(", ", new[] { m.IncidentAt is { } at ? DocumentFormat.DateTime(at) : null, m.IncidentPlace }.Where(s => s is not null))),
                ("Conscious on arrival", m.ConsciousOnArrival is { } conscious ? (conscious ? "Yes" : "No") : null),
                ("Smell of alcohol", m.SmellOfAlcohol is { } alcohol ? (alcohol ? "Yes" : "No") : null),
                ("Police informed", m.IntimatedAt is { } i ? $"{DocumentFormat.DateTime(i)}, {m.IntimationMode}, received by {m.ReceivedBy}, {m.PoliceStation}" : $"Not yet ({m.PoliceStation})"),
                ("Police entry no.", m.PoliceEntryNumber),
            ])}
            {DocumentParts.Title("Examination")}
            {(signed is null
                ? Html.Of($"<p>Injuries as noted in the MLC register: {m.Injuries}</p><p class=\"muted\">No signed wound certificate is on the record.</p>")
                : Html.Of($"<p>Examined on {DocumentFormat.DateTime(signed.ExaminedAt)}. Marks of identification: {signed.IdentificationMarks}.</p>{Injuries(signed)}"))}
            {Examination(m)}
            {DocumentParts.Title("Treatment and outcome")}
            {DocumentParts.Fields([
                ("Seen by a doctor", c.DoctorSeenAt is { } seen ? $"{DocumentFormat.DateTime(seen)}, {c.DoctorName}" : null),
                ("Outcome", c.Disposition is { } d ? Outcome(d, c.ReferredTo) : "Under treatment in the emergency department"),
                ("On", c.DisposedAt is { } disposed ? DocumentFormat.DateTime(disposed) : null),
            ])}
            <p>The facts above are taken from the hospital's medico-legal record of this case. The opinions on the injuries are those of the
            examining doctor who signed the wound certificate. Made on {DocumentFormat.DateTime(now)}.</p>
            {(signed is null ? Html.Empty : Signatory(r, signed, signature))}
            {DocumentParts.Signatories([new DocumentSignatory("Medical Superintendent", null, null, null)])}
            """);
        return new PrintableDocument
        {
            Title = $"Medico-legal report, MLC {m.Number}",
            FileName = $"medico-legal-report-{m.Number}",
            Body = body,
            Styles = DocumentParts.Styles + Styles,
            Footer = $"{m.Number} · Medico-legal: confidential",
            Template = letterhead.Template,
        };
    }

    private static Html Injuries(WoundCertificate w)
    {
        var rows = w.Injuries.Select(i => Html.Of($"""
            <tr><td class="num">{i.Number}</td><td>{Words(i.Kind)}</td><td>{i.Site}</td><td>{i.Size ?? DocumentFormat.Missing}</td><td>{i.Description}</td>
            <td>{Words(i.Weapon)}</td><td>{i.Age}</td><td>{Domain.Emergency.WoundCertificate.Describe(i)}</td></tr>
            """));
        return Html.Of($"""
            <table class="injuries">
              <thead><tr><th>#</th><th>Injury</th><th>Site</th><th>Size</th><th>Description</th><th>Weapon</th><th>Age</th><th>Nature (BNS 2023)</th></tr></thead>
              <tbody>{Html.Join(rows)}</tbody>
            </table>
            """);
    }

    // A sexual-assault victim's examination (BNSS 2023, s.184), as the MLC record keeps it.
    private static Html Examination(MlcRecord m) => m is { ExaminationStartedAt: { } started, ExaminationEndedAt: { } ended }
        ? Html.Of($"""
            {DocumentParts.Fields([
                ("Examined (BNSS s.184) by", m.ExaminerRegistration is { } reg ? $"{m.ExaminerName} (Reg. {reg})" : m.ExaminerName),
                ("Began", DocumentFormat.DateTime(started)),
                ("Ended", DocumentFormat.DateTime(ended)),
                ("Consent given by", m.ConsentGivenBy),
                ("Report to the investigating officer", m.ReportForwardedAt is { } at ? $"{DocumentFormat.DateTime(at)}, to {m.InvestigatingOfficer}" : "Not yet sent"),
            ])}
            """)
        : Html.Empty;

    private static Html Signatory(MedicoLegalRecord r, WoundCertificate w, ImageData? signature) => w.IsSigned
        ? Html.Of($"""
            {DocumentParts.Signatories([new DocumentSignatory(w.DoctorName ?? string.Empty, r.Doctor?.Qualification, w.DoctorRegistration, signature)])}
            <p class="muted">Signed by the examining doctor in HC360 on {DocumentFormat.DateTime(w.SignedAt!.Value)}.</p>
            """)
        : DocumentParts.Signatories([new DocumentSignatory("Examining doctor", null, null, null)]);

    private static string? Age(MedicoLegalRecord r) =>
        r.Patient.DateOfBirth is { } dob ? $"{(int)((r.Entry.IdentifiedAt.Date - dob.ToDateTime(TimeOnly.MinValue)).TotalDays / 365.2425)} years" : null;

    private static string BroughtBy(MlcRecord m) => m.BroughtByName is { } name ? $"{m.BroughtBy}: {name}" : m.BroughtBy.ToString();

    private static string Outcome(EmergencyDisposition d, string? referredTo) => d switch
    {
        EmergencyDisposition.Discharged => "Treated and discharged",
        EmergencyDisposition.Admitted => "Admitted",
        EmergencyDisposition.ReferredOut => $"Referred to {referredTo}",
        EmergencyDisposition.Lama => "Left against medical advice",
        EmergencyDisposition.Absconded => "Absconded",
        _ => "Died",
    };

    private static string Words(InjuryKind kind) => kind switch
    {
        InjuryKind.IncisedWound => "Incised wound",
        InjuryKind.StabWound => "Stab wound",
        InjuryKind.ChopWound => "Chop wound",
        InjuryKind.FirearmWound => "Firearm wound",
        _ => kind.ToString(),
    };

    private static string Words(InjuryWeapon weapon) => weapon switch
    {
        InjuryWeapon.SharpPointed => "Sharp-pointed",
        InjuryWeapon.HeavySharp => "Heavy sharp-edged",
        _ => weapon.ToString(),
    };

    private const string Styles = """
        table.injuries { width: 100%; border-collapse: collapse; margin: 8px 0; font-size: 9pt; }
        table.injuries th, table.injuries td { border: 1px solid #999; padding: 3px 4px; text-align: left; vertical-align: top; }
        table.injuries td.num { text-align: right; }
        """;
}
