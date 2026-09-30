using HyFib.HealthCare360.Modules.Privacy.Features.Breaches;
using HyFib.HealthCare360.Modules.Privacy.Features.Portal;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Modules.Privacy.Features.Retention;
using HyFib.HealthCare360.Modules.Privacy.Features.Settings;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy;

public static class PrivacyPermissions
{
    public const string RequestsRead = "privacy.requests.read";

    /// <summary>Verify the requester, answer, correct and erase: the grievance officer's work.</summary>
    public const string RequestsManage = "privacy.requests.manage";

    /// <summary>Record a request or a nominee for a patient at the desk.</summary>
    public const string RequestsLog = "privacy.requests.log";

    public const string BreachesManage = "privacy.breaches.manage";
    public const string RetentionManage = "privacy.retention.manage";
    public const string SettingsManage = "privacy.settings.manage";

    private static readonly string[] Officers = [SystemRoles.HospitalAdmin, SystemRoles.QualityManager, SystemRoles.MrdOfficer];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(RequestsRead, "Privacy", "View patients' privacy requests (access, correction, erasure, grievance)", Officers),
        new(RequestsManage, "Privacy", "Verify requesters, answer privacy requests, and correct or erase data for them", Officers),
        new(RequestsLog, "Privacy", "Record privacy requests and nominees for patients at the desk",
            [SystemRoles.FrontOffice, .. Officers]),
        new(BreachesManage, "Privacy", "Record personal data breaches, report them to the Board and notify the people affected",
            [SystemRoles.HospitalAdmin, SystemRoles.QualityManager]),
        new(RetentionManage, "Privacy", "Set how long each kind of data is kept, and run the clean-up", [SystemRoles.HospitalAdmin]),
        new(SettingsManage, "Privacy", "Set the grievance officer's contact details and the response period", [SystemRoles.HospitalAdmin]),
    ];
}

/// <summary>
/// DPDP Act 2023 duties (SRS PRV-01..04): data-principal requests and grievances with a published response period,
/// nominees, the personal-data breach register with the 72-hour report, and the retention schedule with its nightly
/// clean-up.
/// </summary>
public sealed class PrivacyModule : IModule
{
    public string Name => "privacy";

    public IEnumerable<PermissionDefinition> Permissions => PrivacyPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<PrivacySettings>();
        services.AddScoped<DataRequestService>();
        services.AddScoped<PersonalDataInventory>();
        services.AddScoped<ErasureService>();
        services.AddScoped<NomineeService>();
        services.AddScoped<BreachService>();
        services.AddScoped<RetentionService>();
        services.AddHostedService<RetentionJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RequestEndpoints.Map(endpoints);
        BreachEndpoints.Map(endpoints);
        RetentionEndpoints.Map(endpoints);
        PrivacySettingsEndpoints.Map(endpoints);
        PortalPrivacyEndpoints.Map(endpoints);
    }
}
