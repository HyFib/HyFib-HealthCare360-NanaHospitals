using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Patients;

public static class PatientsPermissions
{
    public const string Read = "patients.read";
    public const string Register = "patients.register";
    public const string Merge = "patients.merge";
    public const string ReadRestricted = "patients.restricted.read";
    public const string BreakGlass = "patients.break_glass";
    public const string Consents = "patients.consents";
    public const string Documents = "patients.documents";
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
        new(Read, "Patients", "Search and view patient demographics", PlatformPermissions.AllStaff),
        new(Register, "Patients", "Register patients and edit demographics", [.. Desk, SystemRoles.Nurse, SystemRoles.Billing, SystemRoles.InsuranceDesk]),
        new(Merge, "Patients", "Merge duplicate patient records", [SystemRoles.HospitalAdmin, SystemRoles.MrdOfficer]),
        new(ReadRestricted, "Patients", "Open restricted (VIP/confidential) records without break-the-glass", [SystemRoles.HospitalAdmin]),
        new(BreakGlass, "Patients", "Emergency access to restricted records (audited)", Clinical),
        new(Consents, "Patients", "Capture and revoke patient consents", [.. Desk, .. Clinical]),
        new(Documents, "Patients", "Attach and view patient documents", [.. Desk, .. Clinical, SystemRoles.MrdOfficer, SystemRoles.InsuranceDesk]),
        new(VisitsRead, "Visits", "View visits and queues", PlatformPermissions.AllStaff),
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
        services.AddScoped<VisitService>();
        services.AddScoped<PortalPatientScope>();
        services.AddScoped<PortalFamilyService>();
        services.AddScoped<IPortalAccountResolver, PortalAccountResolver>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        PatientEndpoints.Map(endpoints);
        VisitEndpoints.Map(endpoints);
        QueueEndpoints.Map(endpoints);
        FrontOfficeEndpoints.Map(endpoints);
        PortalFamilyEndpoints.Map(endpoints);
    }
}
