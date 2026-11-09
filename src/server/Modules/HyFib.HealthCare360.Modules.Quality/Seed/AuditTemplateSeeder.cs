using HyFib.HealthCare360.Modules.Quality.Domain.Audits;

namespace HyFib.HealthCare360.Modules.Quality.Seed;

/// <summary>
/// One starting checklist per NABH chapter, so the quality team can schedule internal audits on the first day. The items
/// are common audit questions for each chapter, cited by chapter only: the hospital adapts them to its own NABH
/// self-assessment and cites the objective elements it audits against. Idempotent by code; a checklist the hospital edited
/// is never touched again.
/// </summary>
internal sealed class AuditTemplateSeeder : ISeeder
{
    public int Order => 121;

    private static readonly (string Code, NabhChapter Chapter, string Title, string[] Items)[] Templates =
    [
        ("AAC-BASIC", NabhChapter.Aac, "Access, assessment and continuity of care",
        [
            "Registration and admission follow the documented process, and the patient is told the expected cost",
            "Initial assessment of in-patients is documented within the time the hospital defines",
            "Emergency patients are triaged and assessed by a doctor without delay",
            "Laboratory and imaging results are reported within the defined turnaround time, critical results communicated and recorded",
            "The discharge summary is given at discharge, with follow-up advice in a language the patient understands",
            "Transfers in and out are documented with the reason and the patient's condition",
        ]),
        ("COP-BASIC", NabhChapter.Cop, "Care of patients",
        [
            "Care is planned and the plan is documented and reviewed by the treating doctor",
            "High-risk patients (falls, pressure injury) are assessed on admission and reassessed, with preventive measures in place",
            "Informed consent is taken before procedures, anaesthesia and transfusion",
            "The WHO surgical safety checklist is completed for every operation",
            "Transfusion is given with bedside checks by two staff and monitored",
            "Resuscitation equipment is checked and the code team responds within the defined time",
        ]),
        ("MOM-BASIC", NabhChapter.Mom, "Management of medication",
        [
            "Prescriptions are legible, dated, timed and signed, with the dose, route and frequency",
            "High-alert medicines are stored apart, labelled and double-checked before administration",
            "Look-alike, sound-alike medicines are identified and stored apart",
            "Medicines are stored at the right temperature, with the temperature log maintained",
            "Expired and near-expiry medicines are identified and removed from use",
            "Narcotic and psychotropic medicines are kept under double lock with an up-to-date register",
            "Adverse drug reactions are reported and reviewed",
        ]),
        ("PRE-BASIC", NabhChapter.Pre, "Patient rights and education",
        [
            "Patient rights and responsibilities are displayed in the local language",
            "Privacy is maintained during examination and care",
            "Patients and families are educated about their condition and care, and it is documented",
            "Complaints are received, acknowledged and resolved within the defined time",
            "The estimated cost of treatment is explained before treatment",
        ]),
        ("HIC-BASIC", NabhChapter.Hic, "Hospital infection control",
        [
            "Hand hygiene facilities are available at the point of care and staff comply",
            "Biomedical waste is segregated at source in the right colour-coded containers",
            "Bundles for central lines, urinary catheters and ventilated patients are followed and documented",
            "Sterilised items are stored correctly and within their expiry",
            "Isolation precautions are followed for patients who need them",
            "Personal protective equipment is available and used correctly",
            "Spill management kits are available and staff know how to use them",
        ]),
        ("PSQ-BASIC", NabhChapter.Psq, "Patient safety and quality improvement",
        [
            "Incidents are reported, and staff know how to report one (anonymously if they wish)",
            "Sentinel events are analysed (root-cause analysis) within the defined time",
            "Quality indicators are collected, analysed and acted on",
            "Corrective and preventive actions from audits and incidents are tracked to closure",
            "Patient identification uses two identifiers before every procedure, medication and sample",
        ]),
        ("ROM-BASIC", NabhChapter.Rom, "Responsibilities of management",
        [
            "The hospital's mission, vision and values are displayed and known to staff",
            "Statutory licences are valid and displayed",
            "Management reviews the quality and safety reports at defined intervals",
            "Contracted services are monitored against their agreed standards",
        ]),
        ("FMS-BASIC", NabhChapter.Fms, "Facility management and safety",
        [
            "Fire exits are marked, unobstructed and fire extinguishers within their service date",
            "A fire drill has been held within the defined interval and recorded",
            "Medical equipment has a preventive maintenance and calibration record",
            "Medical gas supply has alarms and a backup, checked and recorded",
            "Electrical safety: panels closed, no exposed wiring, earthing checked",
            "Hazardous materials are labelled and their safety data sheets available",
        ]),
        ("HRM-BASIC", NabhChapter.Hrm, "Human resource management",
        [
            "Staff files hold the credentials, verified, and current registrations",
            "New staff complete induction training, documented",
            "Staff are trained in basic life support and fire safety, with records",
            "Job descriptions are available for each role",
            "Occupational health: staff vaccinated (hepatitis B) and needle-stick injuries followed up",
        ]),
        ("IMS-BASIC", NabhChapter.Ims, "Information management system",
        [
            "Medical records are complete, legible, dated, timed and signed",
            "Abbreviations used are from the approved list",
            "Medical records are kept confidential, with access controlled",
            "Records are retained for the defined period and their destruction documented",
            "Downtime procedures are known and drilled",
        ]),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.Set<AuditTemplate>().Select(t => t.Code).ToListAsync(cancellationToken);
        foreach (var (code, chapter, title, items) in Templates.Where(t => !existing.Contains(t.Code)))
        {
            db.Add(new AuditTemplate
            {
                Code = code,
                Chapter = chapter,
                Title = title,
                Description = "A starting checklist: adapt the items to the hospital's NABH self-assessment and cite the objective elements audited.",
                Items = items.Select((text, index) => new AuditItem($"I{index + 1:00}", text, chapter.ToString().ToUpperInvariant())).ToList(),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
