using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Documents;

/// <summary>
/// PCPNDT Form F, as the Appropriate Authority receives it: the clinic and its registration, Section A (the woman and the
/// referral), Section B (the ultrasound, its indications and result) and Section D (both declarations). A form that is not
/// completed says so across the page.
/// </summary>
internal static class FormFDocument
{
    private const string Styles = """
        .section { margin-top: 10px; }
        .section h2 { font-size: 11pt; margin: 0 0 4px; }
        .declaration { border: 1px solid #999; padding: 6px 8px; margin-top: 6px; font-size: 9.5pt; }
        ol.indications { margin: 2px 0 0 18px; padding: 0; }

        """;

    public static PrintableDocument Build(FormFDto form, DocumentLetterhead letterhead)
    {
        var title = $"Form F {form.FormNumber}";
        var completed = form.Status == FormFStatus.Completed;
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("FORM F — Pre-Natal Diagnosis (ultrasonography)")}
            {DocumentParts.Banner(completed ? null : "NOT COMPLETED — the result and the doctor's declaration are not yet recorded")}
            {DocumentParts.Fields([
                ("PCPNDT registration no.", form.RegistrationNumber),
                ("Form no.", form.FormNumber),
                ("Accession no.", form.AccessionNumber),
            ])}
            <section class="section">
              <h2>Section A</h2>
              {DocumentParts.Fields([
                  ("Name of the woman", form.PatientName),
                  ("UHID", form.Uhid),
                  ("Age", $"{form.AgeYears} years"),
                  ("Husband / father", form.HusbandOrFatherName),
                  ("Address", form.Address),
                  ("Phone", form.Phone),
                  ("Living children", $"{form.LivingSons} son(s), {form.LivingDaughters} daughter(s)"),
                  ("Last menstrual period", form.LastMenstrualPeriod is { } lmp ? DocumentFormat.Date(lmp) : null),
                  ("Weeks of pregnancy", form.WeeksOfPregnancy?.ToString(Html.Culture)),
                  ("Referred by", form.Referral == FormFReferral.Self
                      ? "Self-referral by the doctor performing the ultrasound"
                      : $"{form.ReferringDoctorName}, {form.ReferringDoctorAddress}"),
              ])}
            </section>
            <section class="section">
              <h2>Section B — Non-invasive procedure (ultrasonography)</h2>
              {DocumentParts.Fields([
                  ("Doctor performing the procedure", form.DoctorName),
                  ("Date of the procedure", form.ProcedureDate is { } done ? DocumentFormat.Date(done) : null),
                  ("Result", form.Result),
                  ("Result conveyed to", form.ResultConveyedTo),
                  ("Conveyed on", form.ResultConveyedOn is { } on ? DocumentFormat.Date(on) : null),
                  ("Indication for MTP", form.MtpIndicated ? form.MtpIndication : "None"),
              ])}
              <div><strong>Indications</strong>
                <ol class="indications">{Html.Join(form.Indications.Select(i => Html.Of($"<li>{Indication(i)}</li>")))}</ol>
                {(form.OtherIndication is { } other ? Html.Of($"<div>Other: {other}</div>") : Html.Empty)}
              </div>
            </section>
            <section class="section">
              <h2>Section D — Declarations</h2>
              <div class="declaration">
                I, {form.PatientName}, declare that by undergoing this ultrasonography I do not want to know the sex of my foetus.
                <div>Declared on {DocumentFormat.DateTime(form.DeclaredAt)}{(form.DeclarationRecordedBy is { } by ? $", recorded by {by}" : "")}.</div>
              </div>
              <div class="declaration">
                {(completed
                    ? Html.Of($"I, {form.DoctorName}, declare that while conducting the ultrasonography on {form.PatientName} I have neither detected nor disclosed the sex of her foetus to anybody in any manner. <div>Declared on {DocumentFormat.DateTime(form.CompletedAt!.Value)}.</div>")
                    : Html.Of($"Doctor's declaration not yet made."))}
              </div>
            </section>
            """);

        return new PrintableDocument
        {
            Title = title,
            FileName = $"form-f-{form.FormNumber}",
            Body = body,
            Styles = DocumentParts.Styles + Styles,
            Footer = $"{form.PatientName} · UHID {form.Uhid} · {title}",
            Template = letterhead.Template,
        };
    }

    /// <summary>An indication as Form F words it.</summary>
    public static string Indication(FormFIndication indication) => indication switch
    {
        FormFIndication.IntraUterineOrEctopicPregnancy => "To diagnose intra-uterine and/or ectopic pregnancy and confirm viability",
        FormFIndication.GestationalAge => "Estimation of gestational age (dating)",
        FormFIndication.NumberOfFoetusesAndChorionicity => "Detection of number of foetuses and their chorionicity",
        FormFIndication.PregnancyWithIucdOrContraceptiveFailure => "Suspected pregnancy with IUCD in situ or after contraceptive failure or MTP failure",
        FormFIndication.VaginalBleedingOrLeaking => "Vaginal bleeding or leaking",
        FormFIndication.FollowUpOfAbortion => "Follow-up of cases of abortion",
        FormFIndication.CervicalCanalAndInternalOs => "Assessment of cervical canal and diameter of internal os",
        FormFIndication.UterineSizeDiscrepancy => "Discrepancy between uterine size and period of amenorrhoea",
        FormFIndication.AdnexalOrUterinePathology => "Any suspected adnexal or uterine pathology or abnormality",
        FormFIndication.ChromosomalOrStructuralAbnormalities => "Detection of chromosomal abnormalities, foetal structural defects and other abnormalities, and their follow-up",
        FormFIndication.FoetalPresentationAndPosition => "To evaluate foetal presentation and position",
        FormFIndication.LiquorAmnii => "Assessment of liquor amnii",
        FormFIndication.PretermLabourOrRupturedMembranes => "Preterm labour or preterm premature rupture of membranes",
        FormFIndication.PlacentalEvaluation => "Evaluation of placental position, thickness, grading and abnormalities",
        FormFIndication.UmbilicalCordEvaluation => "Evaluation of umbilical cord: presentation, insertion, nuchal encirclement, number of vessels, true knot",
        FormFIndication.PreviousCaesareanScars => "Evaluation of previous caesarean section scars",
        FormFIndication.FoetalGrowthAndWellBeing => "Evaluation of foetal growth parameters, foetal weight and foetal well-being",
        FormFIndication.ColourFlowAndDoppler => "Colour flow mapping and duplex Doppler studies",
        FormFIndication.GuidedProcedures => "Ultrasound-guided procedures such as MTP or external cephalic version, and their follow-up",
        FormFIndication.AdjunctToInvasiveInterventions => "Adjunct to diagnostic and therapeutic invasive interventions (CVS, amniocentesis, foetal blood sampling and others)",
        FormFIndication.IntraPartumEvents => "Observation of intra-partum events",
        FormFIndication.ConditionsComplicatingPregnancy => "Medical or surgical conditions complicating pregnancy",
        FormFIndication.Research => "Research or scientific studies in recognised institutions",
        _ => indication.ToString(),
    };
}
