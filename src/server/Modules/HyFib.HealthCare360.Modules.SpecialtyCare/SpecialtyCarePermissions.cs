namespace HyFib.HealthCare360.Modules.SpecialtyCare;

/// <summary>
/// Permissions of the SpecialtyCare module (<c>ot.*</c>, <c>dialysis.*</c>, <c>physio.*</c>, <c>bloodbank.*</c>, <c>mtp.*</c>).
/// Reading is separated from acting so that, for example, ward nurses can see the OT list or blood availability
/// without being able to schedule surgery or issue blood.
/// </summary>
public static class SpecialtyCarePermissions
{
    // ------------------------------------------------------------------ operation theatre (OT-01..07)
    public const string OtRead = "ot.read";
    public const string OtTheatresManage = "ot.theatres.manage";
    public const string OtBookingsManage = "ot.bookings.manage";
    public const string OtChecklists = "ot.checklists.record";
    public const string OtSurgery = "ot.surgery.perform";
    public const string OtAnaesthesia = "ot.anaesthesia.record";
    public const string OtMaterials = "ot.materials.record";
    public const string OtReports = "ot.reports.read";

    // ------------------------------------------------------------------ dialysis (DIA-01..05)
    public const string DialysisRead = "dialysis.read";
    public const string DialysisMachines = "dialysis.machines.manage";
    public const string DialysisPatients = "dialysis.patients.manage";
    public const string DialysisSessions = "dialysis.sessions.manage";

    // ------------------------------------------------------------------ physiotherapy (PHY-01..04)
    public const string PhysioRead = "physio.read";
    public const string PhysioReferrals = "physio.referrals.manage";
    public const string PhysioTreatment = "physio.treatment.manage";

    // ------------------------------------------------------------------ blood bank (BB-01..07)
    public const string BloodBankRead = "bloodbank.read";
    public const string BloodBankDonors = "bloodbank.donors.manage";
    public const string BloodBankInventory = "bloodbank.inventory.manage";
    public const string BloodBankRequests = "bloodbank.requests.create";
    public const string BloodBankCrossmatch = "bloodbank.crossmatch.perform";
    public const string BloodBankIssue = "bloodbank.units.issue";
    public const string BloodBankReactions = "bloodbank.reactions.report";
    public const string BloodBankReactionsInvestigate = "bloodbank.reactions.investigate";

    // ------------------------------------------------------------------ termination of pregnancy (MTP Act; gap R-2c)
    public const string MtpRead = "mtp.read";
    public const string MtpCertify = "mtp.certify";
    public const string MtpRegister = "mtp.register";

    private const string Ot = "Operation Theatre";
    private const string Dialysis = "Dialysis";
    private const string Physio = "Physiotherapy";
    private const string BloodBank = "Blood Bank";
    private const string Mtp = "Termination of pregnancy";

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(OtRead, Ot, "View theatres, the OT calendar and surgery records", [SystemRoles.OtCoordinator, SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.HospitalAdmin]),
        new(OtTheatresManage, Ot, "Add and edit operation theatres", [SystemRoles.OtCoordinator, SystemRoles.HospitalAdmin]),
        new(OtBookingsManage, Ot, "Book, schedule, postpone and cancel surgeries", [SystemRoles.OtCoordinator, SystemRoles.Doctor]),
        new(OtChecklists, Ot, "Record the pre-operative and WHO surgical safety checklists", [SystemRoles.Nurse, SystemRoles.Doctor, SystemRoles.OtCoordinator]),
        new(OtSurgery, Ot, "Start and complete surgery, write operation notes and post-op orders", [SystemRoles.Doctor]),
        new(OtAnaesthesia, Ot, "Write the anaesthesia record", [SystemRoles.Doctor]),
        new(OtMaterials, Ot, "Record implants and consumables used in surgery (billed to the patient)", [SystemRoles.Nurse, SystemRoles.OtCoordinator, SystemRoles.Doctor]),
        new(OtReports, Ot, "View OT utilisation reports", [SystemRoles.OtCoordinator, SystemRoles.HospitalAdmin, SystemRoles.MisAnalyst]),

        new(DialysisRead, Dialysis, "View the dialysis board, patients, machines and sessions", [SystemRoles.DialysisTechnician, SystemRoles.Nurse, SystemRoles.Doctor, SystemRoles.HospitalAdmin]),
        new(DialysisMachines, Dialysis, "Add and edit dialysis machines and stations", [SystemRoles.DialysisTechnician, SystemRoles.HospitalAdmin]),
        new(DialysisPatients, Dialysis, "Enrol dialysis patients and edit their dialysis profile", [SystemRoles.Doctor, SystemRoles.DialysisTechnician, SystemRoles.Nurse]),
        new(DialysisSessions, Dialysis, "Schedule and record dialysis sessions, dialyzers and machine disinfection", [SystemRoles.DialysisTechnician, SystemRoles.Nurse, SystemRoles.Doctor]),

        new(PhysioRead, Physio, "View physiotherapy referrals, plans and sessions", [SystemRoles.Physiotherapist, SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.HospitalAdmin]),
        new(PhysioReferrals, Physio, "Refer patients to physiotherapy and cancel referrals", [SystemRoles.Doctor, SystemRoles.Physiotherapist]),
        new(PhysioTreatment, Physio, "Assess patients, plan treatment and record sessions", [SystemRoles.Physiotherapist]),

        new(BloodBankRead, BloodBank, "View blood stock, blood requests and transfusion records", [SystemRoles.BloodBankOfficer, SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.HospitalAdmin]),
        new(BloodBankDonors, BloodBank, "Register and screen blood donors and record donations (donor data is confidential)", [SystemRoles.BloodBankOfficer]),
        new(BloodBankInventory, BloodBank, "Prepare components, record grouping/TTI results and discard units", [SystemRoles.BloodBankOfficer]),
        new(BloodBankRequests, BloodBank, "Request blood for a patient", [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.BloodBankOfficer]),
        new(BloodBankCrossmatch, BloodBank, "Group patient samples and record cross-matches", [SystemRoles.BloodBankOfficer]),
        new(BloodBankIssue, BloodBank, "Issue blood units and accept returns", [SystemRoles.BloodBankOfficer]),
        new(BloodBankReactions, BloodBank, "Report transfusion reactions", [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.BloodBankOfficer]),
        new(BloodBankReactionsInvestigate, BloodBank, "Investigate and close transfusion reaction reports", [SystemRoles.BloodBankOfficer]),

        // MTP Act s.5A: no one but a person authorised may learn of a termination. No clinical role holds these by default:
        // grant mtp.certify, with patients.sensitive.mtp, to the gynaecologists the place's approval names.
        new(MtpRead, Mtp, "View termination of pregnancy cases (confidential under the MTP Act)", [SystemRoles.MrdOfficer]),
        new(MtpCertify, Mtp, "Record the woman's consent (Form C), the doctors' opinion (Form I) and the termination (Form III)"),
        new(MtpRegister, Mtp, "View and export the MTP admission register (Form III) and the monthly report (Form II)", [SystemRoles.MrdOfficer]),
    ];
}
