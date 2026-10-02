using HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;
using HyFib.HealthCare360.Modules.VirtualCare.Services;

namespace HyFib.HealthCare360.Modules.VirtualCare;

public static class VirtualCarePermissions
{
    public const string TelehealthRead = "telehealth.sessions.read";
    public const string TelehealthManage = "telehealth.sessions.manage";

    /// <summary>Run video consultations: join as clinician, admit from the waiting room, start and end.</summary>
    public const string TelehealthConsult = "telehealth.consult";

    public const string RpmRead = "rpm.read";
    public const string RpmManage = "rpm.manage";

    /// <summary>Acknowledge and resolve RPM alerts; holders also receive in-app alert notifications.</summary>
    public const string RpmAlerts = "rpm.alerts.manage";

    private static readonly string[] Clinical = [SystemRoles.Doctor, SystemRoles.Nurse];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(TelehealthRead, "Telehealth", "View video consultations, their participants and chat", [.. Clinical, SystemRoles.FrontOffice, SystemRoles.HospitalAdmin]),
        new(TelehealthManage, "Telehealth", "Create and cancel video consultations and resend join links", [.. Clinical, SystemRoles.FrontOffice]),
        new(TelehealthConsult, "Telehealth", "Run video consultations (admit patients, start, end, record consent)", [SystemRoles.Doctor]),
        new(RpmRead, "Remote Monitoring", "View monitored patients, readings, trends and alerts", Clinical),
        new(RpmManage, "Remote Monitoring", "Enrol patients, set thresholds, register devices and record readings", [.. Clinical, SystemRoles.HospitalAdmin]),
        new(RpmAlerts, "Remote Monitoring", "Receive, acknowledge and resolve remote monitoring alerts", Clinical),
    ];
}

/// <summary>
/// Telehealth video consultations (TEL-01..06) and remote patient monitoring (RPM-01..05). Follows Scheduling's
/// <c>AppointmentBooked</c>/<c>AppointmentCancelled</c> to create and cancel video sessions, links telehealth visits
/// (<c>VisitCreated</c>) and tells patients when a teleconsultation prescription is signed (<c>PrescriptionSigned</c>).
/// </summary>
public sealed class VirtualCareModule : IModule
{
    public string Name => "virtualcare";

    public IEnumerable<PermissionDefinition> Permissions => VirtualCarePermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        VirtualCareRealtimeTopics.Register(services);
        services.AddScoped<VirtualCareSettings>();
        services.AddScoped<TelehealthLinkSender>();
        services.AddScoped<TelehealthBroadcaster>();
        services.AddScoped<TelehealthSessionService>();
        services.AddScoped<RpmEnrollmentService>();
        services.AddScoped<RpmAlertService>();
        services.AddScoped<RpmIngestionService>();
        services.AddScoped<RpmInsights>();
        services.AddScoped<VirtualCareMonitor>();
        services.AddHostedService<VirtualCareJobs>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        TelehealthEndpoints.Map(endpoints);
        PortalTelehealthEndpoints.Map(endpoints);
        RpmEnrollmentEndpoints.Map(endpoints);
        RpmDeviceEndpoints.Map(endpoints);
        RpmAlertEndpoints.Map(endpoints);
        RpmIngestEndpoints.Map(endpoints);
        // No switch on the hub itself: guests and patients connect without a facility, so a gate here could only read the
        // hospital-wide switch. Every way to join checks video consultations for the consultation's own facility, and
        // nothing else works without joining.
        endpoints.MapHub<TelehealthHub>(TelehealthHub.Path);
    }
}
