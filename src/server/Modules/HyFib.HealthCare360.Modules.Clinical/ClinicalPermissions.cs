namespace HyFib.HealthCare360.Modules.Clinical;

/// <summary>
/// Permissions declared by the Clinical module. EMR/e-prescribing permissions use the <c>clinical.</c> prefix,
/// medical-records work uses <c>mrd.</c> and master health checkups use <c>healthcheck.</c>.
/// </summary>
public static class ClinicalPermissions
{
    public const string EmrRead = "clinical.emr.read";

    /// <summary>
    /// The patient banner's safety flags alone (allergies and current alerts, EHR-03), for the clinical staff who treat or
    /// test the patient without reading the chart: lab, imaging, pharmacy, theatre, dialysis, physiotherapy, blood bank, diet.
    /// </summary>
    public const string FlagsRead = "clinical.flags.read";

    public const string EmrWrite = "clinical.emr.write";
    public const string EncountersSign = "clinical.encounters.sign";
    public const string VitalsRecord = "clinical.vitals.record";
    public const string AllergiesManage = "clinical.allergies.manage";

    /// <summary>Banner alerts (DNR, fall risk, isolation…) are set by nurses as well as doctors, so they are separate from EMR writing.</summary>
    public const string AlertsManage = "clinical.alerts.manage";

    public const string ImmunizationsRecord = "clinical.immunizations.record";
    public const string OrdersPlace = "clinical.orders.place";
    public const string PrescriptionsWrite = "clinical.prescriptions.write";
    public const string PrescriptionsSign = "clinical.prescriptions.sign";

    /// <summary>Issue an authorised refill of a signed prescription (doctors and pharmacists; ERX-05).</summary>
    public const string PrescriptionsRefill = "clinical.prescriptions.refill";

    public const string DischargeSummaryWrite = "clinical.discharge_summary.write";
    public const string TemplatesManage = "clinical.templates.manage";
    public const string MrdCoding = "mrd.coding";
    public const string MrdRecords = "mrd.records";

    /// <summary>
    /// Import the WHO ICD-10 and CMS ICD-10-PCS files into the local catalogues. Separate from coding because an import
    /// changes the code lists every doctor and coder uses.
    /// </summary>
    public const string CatalogueImport = "mrd.catalogue.import";

    public const string HealthCheckManage = "healthcheck.manage";

    /// <summary>Record a birth in the hospital (Form 1, or Form 3 for a still birth).</summary>
    public const string BirthsRecord = "mrd.births.record";

    /// <summary>Certify the cause of a death (MCCD, Form 4): the doctor who attended the patient in the last illness.</summary>
    public const string DeathsCertify = "mrd.deaths.certify";

    /// <summary>The births and deaths registers, and reporting each event to the Registrar.</summary>
    public const string VitalRegister = "mrd.vital.register";

    /// <summary>The IDSP register: notifiable-disease cases, the weekly P and L forms to IHIP, and the outbreak alerts.</summary>
    public const string IdspReport = "mrd.idsp.report";

    /// <summary>The ER board: the patients in the emergency department, most urgent first, with their waits.</summary>
    public const string ErBoard = "clinical.er.board";

    /// <summary>Triage an emergency patient on the Emergency Severity Index (the triage vitals go to the chart).</summary>
    public const string ErTriage = "clinical.er.triage";

    /// <summary>See an emergency patient as their doctor, and decide where they go (discharge, admit, refer…).</summary>
    public const string ErDisposition = "clinical.er.disposition";

    /// <summary>Enter a medico-legal case in the MLC register and record the police intimation.</summary>
    public const string ErMlc = "clinical.er.mlc";

    /// <summary>
    /// Approve or reject restricted antibiotics and review courses: the antimicrobial stewardship team (a clinical
    /// microbiologist by default; grant it to the infectious-diseases physician and the clinical pharmacist the hospital names).
    /// </summary>
    public const string AmsApprove = "clinical.ams.approve";

    /// <summary>The stewardship queue and report: who waits for approval or review.</summary>
    public const string AmsRead = "clinical.ams.read";

    /// <summary>The MLC register, for medical records and the administration that answer the police and the courts.</summary>
    public const string MlcRegister = "mrd.mlc.register";

    private const string Emr = "EMR / Physician Desktop";
    private const string Prescribing = "E-Prescribing";
    private const string Records = "Medical Records";
    private const string Checkups = "Health Checkups";
    private const string Surveillance = "Disease Surveillance";
    private const string Emergency = "Emergency";
    private const string Stewardship = "Antimicrobial Stewardship";

    /// <summary>Everyone who treats, tests, feeds or supplies the patient, and so must see allergies and alerts.</summary>
    private static readonly string[] ClinicalStaff =
    [
        SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.MrdOfficer, SystemRoles.Pharmacist, SystemRoles.LabTechnician, SystemRoles.Pathologist,
        SystemRoles.Radiographer, SystemRoles.Radiologist, SystemRoles.CardiologyTechnician, SystemRoles.Cardiologist, SystemRoles.OtCoordinator,
        SystemRoles.DialysisTechnician, SystemRoles.Physiotherapist, SystemRoles.BloodBankOfficer, SystemRoles.Dietitian,
    ];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(EmrRead, Emr, "View the clinical record: encounters, vitals, allergies, problems, orders, prescriptions and timeline",
            [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.MrdOfficer]),
        new(FlagsRead, Emr, "See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner", ClinicalStaff),
        new(EmrWrite, Emr, "Write encounters (SOAP), diagnoses, clinical notes, procedures, referrals and health-checkup summaries", [SystemRoles.Doctor]),
        new(EncountersSign, Emr, "Sign and amend encounters and clinical notes", [SystemRoles.Doctor]),
        new(VitalsRecord, Emr, "Record vital signs", [SystemRoles.Doctor, SystemRoles.Nurse]),
        new(AllergiesManage, Emr, "Record and update allergies", [SystemRoles.Doctor, SystemRoles.Nurse]),
        new(AlertsManage, Emr, "Set and clear patient alerts (DNR, fall risk, isolation, pregnancy…)", [SystemRoles.Doctor, SystemRoles.Nurse]),
        new(ImmunizationsRecord, Emr, "Record immunisations", [SystemRoles.Doctor, SystemRoles.Nurse]),
        new(OrdersPlace, Emr, "Place and cancel lab, imaging, cardiology and procedure orders", [SystemRoles.Doctor]),
        new(TemplatesManage, Emr, "Maintain clinical templates, order sets and the drug-safety tables", [SystemRoles.Doctor, SystemRoles.HospitalAdmin]),
        new(PrescriptionsWrite, Prescribing, "Draft e-prescriptions", [SystemRoles.Doctor]),
        new(PrescriptionsSign, Prescribing, "Sign and cancel e-prescriptions (sends them to the pharmacy)", [SystemRoles.Doctor]),
        new(PrescriptionsRefill, Prescribing, "Issue an authorised refill of a signed prescription", [SystemRoles.Doctor, SystemRoles.Pharmacist]),
        new(DischargeSummaryWrite, Prescribing, "Write, sign and amend discharge summaries", [SystemRoles.Doctor]),
        new(MrdCoding, Records, "Clinical coding (ICD-10 / ICD-10-PCS) and verification", [SystemRoles.MrdOfficer]),
        new(MrdRecords, Records, "Chart tracking, deficiencies, record requests, archival and morbidity reports",
            [SystemRoles.MrdOfficer, SystemRoles.HospitalAdmin]),
        new(CatalogueImport, Records, "Import the WHO ICD-10 and CMS ICD-10-PCS catalogue files (changes every user's code lists)",
            [SystemRoles.HospitalAdmin]),
        new(BirthsRecord, Records, "Record births in the hospital (Form 1, and Form 3 for a still birth)",
            [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.MrdOfficer]),
        new(DeathsCertify, Records, "Certify the cause of death (MCCD, Form 4)", [SystemRoles.Doctor]),
        new(VitalRegister, Records, "The births and deaths registers, and reporting them to the Registrar within 21 days",
            [SystemRoles.MrdOfficer, SystemRoles.HospitalAdmin]),
        new(IdspReport, Surveillance, "Notifiable diseases (IDSP): the cases, the weekly P and L forms to IHIP, and the outbreak alerts",
            [SystemRoles.MrdOfficer, SystemRoles.QualityManager]),
        new(ErBoard, Emergency, "The ER board: patients in the emergency department, most urgent first, with their waits",
            [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.FrontOffice, SystemRoles.HospitalAdmin]),
        new(ErTriage, Emergency, "Triage emergency patients on the Emergency Severity Index", [SystemRoles.Nurse, SystemRoles.Doctor]),
        new(ErDisposition, Emergency, "See emergency patients and decide where they go: discharge, admit, refer, LAMA", [SystemRoles.Doctor]),
        new(ErMlc, Emergency, "Enter medico-legal cases in the MLC register and record the police intimation", [SystemRoles.Doctor]),
        new(AmsApprove, Stewardship, "Approve or reject restricted antibiotics and review antibiotic courses", [SystemRoles.Pathologist]),
        new(AmsRead, Stewardship, "The antimicrobial stewardship queue and report",
            [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.Pharmacist, SystemRoles.Pathologist, SystemRoles.QualityManager]),
        new(MlcRegister, Records, "The medico-legal case register", [SystemRoles.MrdOfficer, SystemRoles.HospitalAdmin]),
        new(HealthCheckManage, Checkups, "Maintain health packages, book and run health checkups (including corporate batches)",
            [SystemRoles.HospitalAdmin, SystemRoles.FrontOffice]),
    ];
}
