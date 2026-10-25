using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Features.VitalRegistration;

/// <summary>
/// The hospital's copy of a birth report (Form 1, or Form 3 for a still birth) and of the medical certificate of cause
/// of death (Form 4), with what the Registrar needs. An entry made in error says so across the page.
/// </summary>
internal static class VitalRegistrationDocuments
{
    private const string Styles = """
        .section { margin-top: 10px; }
        .section h2 { font-size: 11pt; margin: 0 0 4px; }
        table.causes { width: 100%; border-collapse: collapse; font-size: 9.5pt; }
        table.causes th, table.causes td { border: 1px solid #999; padding: 3px 6px; text-align: left; }

        """;

    public static PrintableDocument Birth(BirthRecordDto b, DocumentLetterhead letterhead)
    {
        var still = b.Outcome == BirthOutcome.StillBirth;
        var title = still ? $"Still birth {b.SerialNumber}" : $"Birth {b.SerialNumber}";
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title(still ? "FORM 3 — Still birth report" : "FORM 1 — Birth report")}
            {DocumentParts.Banner(Banner(b.Status, b.ErrorReason))}
            {DocumentParts.Fields([
                ("Birth register no.", b.SerialNumber),
                ("Date and time of birth", DocumentFormat.DateTime(b.BornAt)),
                ("Sex", b.Sex.ToString()),
                ("Name of the child", still ? null : b.ChildName ?? "Not yet named"),
                ("Mother", $"{b.MotherName} (UHID {b.MotherUhid}), {b.MotherAgeYears} years"),
                ("Father", b.FatherName),
                ("Place of birth", "This hospital"),
                ("Informant", b.InformantName),
            ])}
            <section class="section">
              <h2>Statistical information</h2>
              {DocumentParts.Fields([
                  ("Birth weight", b.WeightGrams is { } g ? $"{g} g" : null),
                  ("Duration of pregnancy", b.WeeksOfGestation is { } w ? $"{w} weeks" : null),
                  ("Method of delivery", b.Method switch
                  {
                      DeliveryMethod.Caesarean => "Caesarean",
                      DeliveryMethod.ForcepsOrVacuum => "Forceps or vacuum",
                      _ => "Natural",
                  }),
                  ("Type of attention at delivery", b.Attendant switch
                  {
                      BirthAttendant.Doctor => "Doctor",
                      BirthAttendant.NurseOrMidwife => "Nurse or midwife",
                      _ => "Other",
                  }),
                  ("Children born alive to the mother, including this one", b.LiveChildren?.ToString(Html.Culture)),
                  ("Cause of foetal death", b.StillBirthCause),
              ])}
            </section>
            <section class="section">
              <h2>Registration</h2>
              {DocumentParts.Fields([
                  ("To be reported by", DocumentFormat.Date(b.DueBy)),
                  ("Reported on", b.ReportedOn is { } on ? DocumentFormat.Date(on) : "Not yet reported"),
                  ("Registration no.", b.RegistrationNumber),
              ])}
            </section>
            """);
        return Document(title, $"birth-{b.SerialNumber}", body, $"{b.MotherName} · UHID {b.MotherUhid} · {title}", letterhead);
    }

    public static PrintableDocument Death(DeathRecordDto d, DocumentLetterhead letterhead)
    {
        var title = $"MCCD {d.SerialNumber}";
        var lines = new[] { "(a) Immediate cause", "(b) Due to", "(c) Due to", "(d) Underlying cause" };
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("FORM 4 — Medical certificate of cause of death")}
            {DocumentParts.Banner(Banner(d.Status, d.ErrorReason))}
            {DocumentParts.Fields([
                ("Death register no.", d.SerialNumber),
                ("Name of the deceased", $"{d.PatientName} (UHID {d.Uhid})"),
                ("Sex", d.Gender),
                ("Age", d.AgeYears?.ToString(Html.Culture)),
                ("Date and time of death", DocumentFormat.DateTime(d.DiedAt)),
            ])}
            <section class="section">
              <h2>Cause of death</h2>
              <table class="causes">
                <thead><tr><th>Part I</th><th>Condition</th><th>Interval between onset and death</th></tr></thead>
                <tbody>{Html.Join(d.Causes.Select((c, i) => Html.Of($"<tr><td>{lines[Math.Min(i, 3)]}</td><td>{c.Condition}</td><td>{c.Interval}</td></tr>")))}</tbody>
              </table>
              {DocumentParts.Fields([
                  ("Part II — other significant conditions", d.OtherConditions),
                  ("Manner of death", Manner(d.Manner)),
                  ("Medico-legal case", d.MedicoLegal ? "Yes" : "No"),
                  ("If a woman, pregnancy", d.Pregnancy switch
                  {
                      PregnancyAtDeath.NotPregnant => "Not pregnant",
                      PregnancyAtDeath.Pregnant => "Pregnant at the time of death",
                      PregnancyAtDeath.WithinSixWeeksOfDelivery => "Within six weeks of the end of a pregnancy",
                      _ => null,
                  }),
              ])}
            </section>
            <section class="section">
              <h2>Certified by</h2>
              {DocumentParts.Fields([
                  ("Doctor", d.CertifiedBy),
                  ("Registration no.", d.CertifierRegistrationNumber),
                  ("Certified on", DocumentFormat.DateTime(d.CertifiedAt)),
                  ("To be reported by", DocumentFormat.Date(d.DueBy)),
                  ("Registrar's registration no.", d.RegistrationNumber),
              ])}
            </section>
            """);
        return Document(title, $"mccd-{d.SerialNumber}", body, $"{d.PatientName} · UHID {d.Uhid} · {title}", letterhead);
    }

    public static string Manner(DeathManner manner) => manner switch
    {
        DeathManner.Natural => "Natural",
        DeathManner.Accident => "Accident",
        DeathManner.Suicide => "Suicide",
        DeathManner.Homicide => "Homicide",
        DeathManner.PendingInvestigation => "Pending investigation",
        _ => manner.ToString(),
    };

    private static string? Banner(VitalReportStatus status, string? reason) =>
        status == VitalReportStatus.EnteredInError ? $"ENTERED IN ERROR — {reason}" : null;

    private static PrintableDocument Document(string title, string file, Html body, string footer, DocumentLetterhead letterhead) => new()
    {
        Title = title,
        FileName = file,
        Body = body,
        Styles = DocumentParts.Styles + Styles,
        Footer = footer,
        Template = letterhead.Template,
    };
}
