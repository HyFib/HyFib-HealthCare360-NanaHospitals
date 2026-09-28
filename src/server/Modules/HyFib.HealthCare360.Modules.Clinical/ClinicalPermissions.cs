namespace HyFib.HealthCare360.Modules.Clinical;

/// <summary>
/// Permissions declared by the Clinical module. EMR/e-prescribing permissions use the <c>clinical.</c> prefix,
/// medical-records work uses <c>mrd.</c> and master health checkups use <c>healthcheck.</c>.
/// </summary>
public static class ClinicalPermissions
{
    public const string EmrRead = "clinical.emr.read";
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
    public const string HealthCheckManage = "healthcheck.manage";

    private const string Emr = "EMR / Physician Desktop";
    private const string Prescribing = "E-Prescribing";
    private const string Records = "Medical Records";
    private const string Checkups = "Health Checkups";

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(EmrRead, Emr, "View the clinical record: encounters, vitals, allergies, problems, orders, prescriptions and timeline",
            [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.MrdOfficer]),
        new(EmrWrite, Emr, "Write encounters (SOAP), diagnoses, clinical notes, referrals and health-checkup summaries", [SystemRoles.Doctor]),
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
        new(HealthCheckManage, Checkups, "Maintain health packages, book and run health checkups (including corporate batches)",
            [SystemRoles.HospitalAdmin, SystemRoles.FrontOffice]),
    ];
}
