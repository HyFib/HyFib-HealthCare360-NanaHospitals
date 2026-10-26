using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Identity;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Patients;

public static class PatientsPermissions
{
    public const string Read = "patients.read";
    public const string Register = "patients.register";
    public const string Merge = "patients.merge";
    public const string ReadRestricted = "patients.restricted.read";

    /// <summary>
    /// See patients' phone numbers, email, street address and emergency contact's phone in full (SRS PRV-03); without it they
    /// are masked. Those who contact or bill patients hold it, and editing a patient's details needs it.
    /// </summary>
    public const string ContactRead = "patients.contact.read";
    public const string BreakGlass = "patients.break_glass";

    /// <summary>Open the clinical record of a patient in the HIV category (<see cref="Domain.SensitiveCategory"/>).</summary>
    public const string SensitiveHiv = "patients.sensitive.hiv";

    /// <summary>Open the clinical record of a patient in the mental health category.</summary>
    public const string SensitiveMentalHealth = "patients.sensitive.mental_health";

    /// <summary>Open the clinical record of a patient in the MTP category. Medical records officers hold it by default: grant it to gynaecologists.</summary>
    public const string SensitiveMtp = "patients.sensitive.mtp";

    /// <summary>Put a patient's record in a sensitive category, or take it out, with a reason.</summary>
    public const string SensitiveManage = "patients.sensitive.manage";
    public const string Consents = "patients.consents";
    public const string Documents = "patients.documents";
    public const string IdentityVerify = "patients.identity.verify";
    public const string VisitsRead = "visits.read";
    public const string VisitsManage = "visits.manage";
    public const string QueueManage = "queue.manage";
    public const string QueueDisplay = "queue.display";
    public const string Enquiries = "frontoffice.enquiries";
    public const string Visitors = "frontoffice.visitors";
    public const string Dashboard = "frontoffice.dashboard";

    /// <summary>Patient Portal access (granted to the Patient role; data is always scoped by <see cref="Services.PortalPatientScope"/>).</summary>
    public const string Portal = "portal.access";

    private static readonly string[] Clinical = [SystemRoles.Doctor, SystemRoles.Nurse];
    private static readonly string[] Desk = [SystemRoles.FrontOffice, SystemRoles.HospitalAdmin];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(Read, "Patients", "Search and view patient demographics", PlatformPermissions.PatientFacingStaff),
        new(Register, "Patients", "Register patients and edit demographics", [.. Desk, SystemRoles.Nurse, SystemRoles.Billing, SystemRoles.InsuranceDesk]),
        new(Merge, "Patients", "Merge duplicate patient records", [SystemRoles.HospitalAdmin, SystemRoles.MrdOfficer]),
        new(ReadRestricted, "Patients", "Open restricted (VIP/confidential) records without break-the-glass", [SystemRoles.HospitalAdmin]),
        new(ContactRead, "Patients", "See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient",
        [
            .. Desk, .. Clinical, SystemRoles.Billing, SystemRoles.Cashier, SystemRoles.InsuranceDesk, SystemRoles.CrmExecutive,
            SystemRoles.MrdOfficer,
        ]),
        new(BreakGlass, "Patients", "Emergency access to restricted records (audited)", Clinical),
        new(SensitiveHiv, "Patients", "Open the clinical record of patients in the HIV category", [.. Clinical, SystemRoles.MrdOfficer]),
        new(SensitiveMentalHealth, "Patients", "Open the clinical record of patients in the mental health category", [.. Clinical, SystemRoles.MrdOfficer]),
        new(SensitiveMtp, "Patients", "Open the clinical record of patients in the termination-of-pregnancy (MTP) category", [SystemRoles.MrdOfficer]),
        new(SensitiveManage, "Patients", "Put a patient's record in a sensitive category (HIV, mental health, MTP) or take it out", [SystemRoles.Doctor]),
        new(Consents, "Patients", "Capture and revoke patient consents", [.. Desk, .. Clinical]),
        new(Documents, "Patients", "Attach and view patient documents", [.. Desk, .. Clinical, SystemRoles.MrdOfficer, SystemRoles.InsuranceDesk]),
        new(IdentityVerify, "Patients", "Check a patient's identity with their Aadhaar fingerprint (UIDAI) or their ABHA (a code from ABDM)", Desk),
        new(VisitsRead, "Visits", "View visits and queues", PlatformPermissions.PatientFacingStaff),
        new(VisitsManage, "Visits", "Create, cancel and complete visits", [.. Desk, .. Clinical]),
        new(QueueManage, "Visits", "Call, start and complete queue tokens", [.. Desk, .. Clinical]),
        new(QueueDisplay, "Visits", "Show the queue display board (kiosk/TV)", Desk),
        new(Enquiries, "Front Office", "Record and resolve enquiries", [.. Desk, SystemRoles.CrmExecutive]),
        new(Visitors, "Front Office", "Issue and close visitor passes", [.. Desk, SystemRoles.Nurse]),
        new(Dashboard, "Front Office", "View the front-office dashboard", Desk),
        new(Portal, "Patient Portal", "Use the patient portal (own and family records only)", [SystemRoles.Patient]),
    ];
}

/// <summary>Patient administration and Front Office (SRS FO-01..06, PMS-03, EHR-06).</summary>
public sealed class PatientsModule : IModule
{
    public string Name => "patients";

    public IEnumerable<PermissionDefinition> Permissions => PatientsPermissions.All;

    public IEnumerable<NumberSeriesDefinition> NumberSeries => PatientsNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        PatientsRealtimeTopics.Register(services);
        services.AddScoped<PatientRegistrationService>();
        services.AddScoped<Seed.LoadTestPatients>();
        services.AddScoped<PatientAccessGuard>();
        services.AddScoped<PatientDeaths>();
        services.AddScoped<PatientContactAccess>();
        services.AddScoped<IPatientRecordAccess>(sp => sp.GetRequiredService<PatientAccessGuard>());
        services.AddScoped<VisitService>();
        services.AddScoped<PortalPatientScope>();
        services.AddScoped<PortalFamilyService>();
        services.AddScoped<IPortalAccountResolver, PortalAccountResolver>();
        services.AddScoped<IPatientLanguageSource, PatientLanguageSource>();
        services.AddScoped<IdentityCheckService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        PatientEndpoints.Map(endpoints);
        VisitEndpoints.Map(endpoints);
        QueueEndpoints.Map(endpoints);
        FrontOfficeEndpoints.Map(endpoints);
        PortalFamilyEndpoints.Map(endpoints);
        IdentityCheckEndpoints.Map(endpoints);
    }
}
