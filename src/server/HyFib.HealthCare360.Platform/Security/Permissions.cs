namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// A grantable capability. Modules declare their own; roles aggregate them. <paramref name="DefaultRoles"/>
/// are granted this permission once when it first appears (admins may revoke it afterwards).
/// </summary>
public sealed record PermissionDefinition(string Name, string Group, string Description, string[]? DefaultRoles = null);

/// <summary>Built-in role names seeded on first start. Administrators can create more.</summary>
public static class SystemRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string HospitalAdmin = "HospitalAdmin";
    public const string Doctor = "Doctor";
    public const string Nurse = "Nurse";
    public const string FrontOffice = "FrontOffice";
    public const string Billing = "Billing";
    public const string Cashier = "Cashier";
    public const string InsuranceDesk = "InsuranceDesk";
    public const string Pharmacist = "Pharmacist";
    public const string LabTechnician = "LabTechnician";
    public const string Pathologist = "Pathologist";
    public const string Radiographer = "Radiographer";
    public const string Radiologist = "Radiologist";
    public const string CardiologyTechnician = "CardiologyTechnician";
    public const string Cardiologist = "Cardiologist";
    public const string StoreKeeper = "StoreKeeper";
    public const string PurchaseOfficer = "PurchaseOfficer";
    public const string MrdOfficer = "MrdOfficer";
    public const string OtCoordinator = "OtCoordinator";
    public const string DialysisTechnician = "DialysisTechnician";
    public const string Physiotherapist = "Physiotherapist";
    public const string BloodBankOfficer = "BloodBankOfficer";
    public const string Dietitian = "Dietitian";
    public const string CanteenManager = "CanteenManager";
    public const string CssdTechnician = "CssdTechnician";
    public const string BiomedicalEngineer = "BiomedicalEngineer";
    public const string HrManager = "HrManager";
    public const string CrmExecutive = "CrmExecutive";
    public const string QualityManager = "QualityManager";
    public const string Accountant = "Accountant";
    public const string MisAnalyst = "MisAnalyst";
    public const string Patient = "Patient";

    public static IReadOnlyList<(string Name, string Description)> All { get; } =
    [
        (SuperAdmin, "Full access to everything, including security settings"),
        (HospitalAdmin, "Hospital administration, masters, users and reports"),
        (Doctor, "Consultants and residents: EMR, orders, e-prescriptions"),
        (Nurse, "Nursing care, vitals, ward operations"),
        (FrontOffice, "Registration, visits, queue and enquiries"),
        (Billing, "OP/IP billing and charge management"),
        (Cashier, "Payments, receipts and day-end closing"),
        (InsuranceDesk, "Insurance verification, pre-authorisation and claims"),
        (Pharmacist, "OP/IP pharmacy dispensing and pharmacy stock"),
        (LabTechnician, "Sample collection, processing and result entry"),
        (Pathologist, "Laboratory result verification and sign-off"),
        (Radiographer, "Imaging acquisition and modality worklist"),
        (Radiologist, "Imaging reporting and sign-off"),
        (CardiologyTechnician, "ECG/Echo/TMT acquisition"),
        (Cardiologist, "Cardiology reporting and sign-off (give cardiologists the Doctor role as well)"),
        (StoreKeeper, "Stores, stock issues and receipts"),
        (PurchaseOfficer, "Purchase requisitions, orders and vendors"),
        (MrdOfficer, "Medical records, coding and record requests"),
        (OtCoordinator, "Operation theatre scheduling and records"),
        (DialysisTechnician, "Dialysis sessions and machines"),
        (Physiotherapist, "Physiotherapy assessment and sessions"),
        (BloodBankOfficer, "Blood donors, inventory and issue"),
        (Dietitian, "Diet orders and meal planning"),
        (CanteenManager, "Canteen menu and sales"),
        (CssdTechnician, "Sterilisation cycles and instrument sets"),
        (BiomedicalEngineer, "Assets, maintenance and calibration"),
        (HrManager, "Employees, attendance, leave and payroll"),
        (CrmExecutive, "Leads, follow-ups, campaigns and feedback"),
        (QualityManager, "Incidents, risk register and quality indicators"),
        (Accountant, "Accounts hand-off, doctor fees and Tally"),
        (MisAnalyst, "MIS reports and BI dashboards"),
        (Patient, "Patient portal user"),
    ];
}

/// <summary>All permissions known to this server (platform + loaded modules).</summary>
public sealed class PermissionRegistry
{
    /// <summary>Grants every permission (Super Administrator only).</summary>
    public const string Wildcard = "*";

    private readonly Dictionary<string, PermissionDefinition> _byName;

    public PermissionRegistry(IEnumerable<PermissionDefinition> definitions)
    {
        _byName = definitions
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IReadOnlyCollection<PermissionDefinition> All => _byName.Values;

    public bool IsDefined(string name) => name == Wildcard || _byName.ContainsKey(name);
}

/// <summary>Permissions owned by the platform (administration, masters, files).</summary>
public static class PlatformPermissions
{
    public const string UsersRead = "admin.users.read";
    public const string UsersManage = "admin.users.manage";
    public const string RolesManage = "admin.roles.manage";
    public const string DevicesManage = "admin.devices.manage";
    public const string AuditRead = "admin.audit.read";
    public const string SettingsManage = "admin.settings.manage";
    public const string IntegrationsManage = "admin.integrations.manage";
    public const string NumberingManage = "admin.numbering.manage";

    /// <summary>Set up how documents print (Hospital setup › Print templates, SRS HMS-04).</summary>
    public const string PrintManage = "admin.print.manage";
    public const string NotificationTemplatesManage = "admin.notifications.manage";
    public const string SystemRead = "admin.system.read";

    /// <summary>Change the server's own credentials, such as its database password. Only SuperAdmin has it by default.</summary>
    public const string SystemManage = "admin.system.manage";
    public const string BackupsManage = "admin.backups.manage";
    public const string MastersRead = "masters.read";
    public const string MastersManage = "masters.manage";
    public const string FilesRead = "files.read";
    public const string FilesWrite = "files.write";

    private static readonly string[] Admins = [SystemRoles.HospitalAdmin];

    /// <summary>Every staff role (patients excluded).</summary>
    public static readonly string[] AllStaff = SystemRoles.All.Select(r => r.Name)
        .Where(r => r is not SystemRoles.Patient and not SystemRoles.SuperAdmin).ToArray();

    public static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(UsersRead, "Administration", "View users and their roles", Admins),
        new(UsersManage, "Administration", "Create, edit, lock and reset users", Admins),
        new(RolesManage, "Administration", "Create roles and assign permissions"),
        new(DevicesManage, "Administration", "Enrol and revoke workstations", Admins),
        new(AuditRead, "Administration", "View and verify the audit trail", [SystemRoles.HospitalAdmin, SystemRoles.QualityManager]),
        new(SettingsManage, "Administration", "Change system settings", Admins),
        new(IntegrationsManage, "Administration", "Configure integrations and credentials"),
        new(NumberingManage, "Administration", "Configure number series (UHID, bill numbers)", Admins),
        new(PrintManage, "Administration", "Set up print templates (letterhead, logo, closing notes)", Admins),
        new(NotificationTemplatesManage, "Administration", "Edit SMS/WhatsApp/e-mail templates", [SystemRoles.HospitalAdmin, SystemRoles.CrmExecutive]),
        new(SystemRead, "Administration", "View server health, WiFi link and backups", Admins),
        new(SystemManage, "Administration", "Change the server's database password"),
        new(BackupsManage, "Administration", "Run backups and download recovery keys"),
        new(MastersRead, "Masters", "View facilities, departments, doctors and services", AllStaff),
        new(MastersManage, "Masters", "Edit facilities, departments, doctors and services", Admins),
        new(FilesRead, "Documents", "Download stored documents", AllStaff),
        new(FilesWrite, "Documents", "Upload documents", AllStaff),
    ];
}
