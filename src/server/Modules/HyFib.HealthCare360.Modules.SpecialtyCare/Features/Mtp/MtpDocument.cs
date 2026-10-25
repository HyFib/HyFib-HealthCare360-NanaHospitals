using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Mtp;

/// <summary>
/// The case's forms as the MTP Regulations 2003 give them: the woman's consent (Form C), the opinion of the doctors
/// (Form I) and, once it is done, the termination as the admission register (Form III) records it. Every page says it is
/// confidential (s.5A); a cancelled case says so across the page.
/// </summary>
internal static class MtpDocument
{
    private const string Styles = """
        .section { margin-top: 10px; }
        .section h2 { font-size: 11pt; margin: 0 0 4px; }
        .statement { border: 1px solid #999; padding: 6px 8px; margin-top: 6px; font-size: 9.5pt; }
        ul.grounds { margin: 2px 0 0 18px; padding: 0; }

        """;

    public static PrintableDocument Build(MtpCaseDto mtp, DocumentLetterhead letterhead)
    {
        var title = $"MTP {mtp.SerialNumber}";
        var consenter = mtp.ConsentBy == MtpConsentBy.Guardian ? $"{mtp.GuardianName} ({mtp.GuardianRelation} of {mtp.PatientName})" : mtp.PatientName;
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("Medical Termination of Pregnancy — CONFIDENTIAL")}
            {DocumentParts.Banner(mtp.Status switch
        {
            MtpCaseStatus.Cancelled => $"NOT DONE — {mtp.CancelReason}",
            MtpCaseStatus.Certified => "The termination is not yet recorded",
            _ => null,
        })}
            {DocumentParts.Fields([
                ("Certificate of approval of the place", mtp.ApprovalNumber),
                ("Admission register serial no.", mtp.SerialNumber),
                ("Name of the woman", mtp.PatientName),
                ("UHID", mtp.Uhid),
                ("Age", $"{mtp.AgeYears} years"),
                ("Duration of pregnancy", $"{mtp.WeeksOfGestation} weeks"),
            ])}
            <section class="section">
              <h2>Form C — Consent</h2>
              <div class="statement">
                I, {consenter}, hereby give my consent to the termination of {(mtp.ConsentBy == MtpConsentBy.Guardian ? "her" : "my")} pregnancy.
                <div>Consent given on {DocumentFormat.Date(mtp.ConsentOn)}{(mtp.ConsentBy == MtpConsentBy.Guardian
                    ? (mtp.AgeYears < MtpCase.AgeOfMajority ? ", the woman being a minor" : ", the woman being mentally ill")
                    : "")}.</div>
              </div>
            </section>
            <section class="section">
              <h2>Form I — Opinion formed by the registered medical practitioner{(mtp.Opinions.Count > 1 ? "s" : "")}</h2>
              <div class="statement">
                {(mtp.Opinions.Count > 1 ? "We hereby certify that we are" : "I hereby certify that I am")} of the opinion, formed in good faith,
                that it is necessary to terminate the pregnancy of {mtp.PatientName}, the pregnancy not exceeding {mtp.WeeksOfGestation} weeks, for the reasons given below:
                <ul class="grounds">{Html.Join(mtp.Grounds.Select(g => Html.Of($"<li>{MtpLabels.Ground(g)}</li>")))}</ul>
                {(mtp.Category is { } category ? Html.Of($"<div>Category under rule 3B: {MtpLabels.Category(category)}</div>") : Html.Empty)}
                {(mtp.MedicalBoardReference is { } board ? Html.Of($"<div>Opinion of the Medical Board: {board}</div>") : Html.Empty)}
              </div>
              {DocumentParts.Fields(mtp.Opinions.Select((o, i) => (
                  $"Doctor {i + 1}", (string?)$"{o.DoctorName}, registration no. {o.RegistrationNumber}, opinion on {DocumentFormat.Date(o.OpinionOn)}")))}
            </section>
            <section class="section">
              <h2>Form III — Admission register</h2>
              {DocumentParts.Fields([
                  ("Date of termination", mtp.TerminatedOn is { } done ? DocumentFormat.Date(done) : null),
                  ("Method", mtp.Method is { } method ? MtpLabels.Method(method) : null),
                  ("Performed by", mtp.PerformedBy),
                  ("Date of discharge", mtp.DischargedOn is { } discharged ? DocumentFormat.Date(discharged) : null),
                  ("Result and remarks", string.Join(". ", new[] { mtp.Outcome, mtp.Remarks }.Where(t => !string.IsNullOrWhiteSpace(t)))),
              ])}
            </section>
            {DocumentParts.End("Confidential under s.5A of the MTP Act: not to be shown to anyone not authorised by law.")}
            """);

        return new PrintableDocument
        {
            Title = title,
            FileName = $"mtp-{mtp.SerialNumber}",
            Body = body,
            Styles = DocumentParts.Styles + Styles,
            Footer = $"CONFIDENTIAL · {title}",
            Template = letterhead.Template,
        };
    }
}
