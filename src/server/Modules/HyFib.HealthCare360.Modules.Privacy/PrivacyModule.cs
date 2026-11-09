using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Features.Breaches;
using HyFib.HealthCare360.Modules.Privacy.Features.Dpdp;
using HyFib.HealthCare360.Modules.Privacy.Features.Portal;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Modules.Privacy.Features.Retention;
using HyFib.HealthCare360.Modules.Privacy.Features.Settings;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Privacy;

public static class PrivacyPermissions
{
    public const string RequestsRead = "privacy.requests.read";

    /// <summary>Verify the requester, answer, correct and erase: the grievance officer's work.</summary>
    public const string RequestsManage = "privacy.requests.manage";

    /// <summary>Record a request or a nominee for a patient at the desk.</summary>
    public const string RequestsLog = "privacy.requests.log";

    public const string BreachesManage = "privacy.breaches.manage";

    /// <summary>
    /// Approve closing a personal data breach, and taking patients off its notification list, asked for by someone else
    /// (MAKER-CHECKER.md, slice 14): the Data Protection Officer. Held by the hospital administrator by default; grant it to
    /// the DPO's or privacy officer's role.
    /// </summary>
    public const string BreachesApprove = "privacy.breaches.approve";
    public const string RetentionManage = "privacy.retention.manage";
    public const string SettingsManage = "privacy.settings.manage";

    /// <summary>The Significant Data Fiduciary duties: the DPO, impact assessments and data audits (DPDP Act s.10).</summary>
    public const string SdfManage = "privacy.sdf.manage";

    /// <summary>Approve an impact assessment someone else wrote: the Data Protection Officer's role.</summary>
    public const string SdfApprove = "privacy.sdf.approve";

    /// <summary>Register the consent managers the hospital accepts consent from (DPDP Act s.6(7)).</summary>
    public const string ConsentManagersManage = "privacy.consent_managers.manage";

    private static readonly string[] Officers = [SystemRoles.HospitalAdmin, SystemRoles.QualityManager, SystemRoles.MrdOfficer];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(RequestsRead, "Privacy", "View patients' privacy requests (access, correction, erasure, grievance)", Officers),
        new(RequestsManage, "Privacy", "Verify requesters, answer privacy requests, and correct or erase data for them", Officers),
        new(RequestsLog, "Privacy", "Record privacy requests and nominees for patients at the desk",
            [SystemRoles.FrontOffice, .. Officers]),
        new(BreachesManage, "Privacy", "Record personal data breaches, report them to the Board and notify the people affected",
            [SystemRoles.HospitalAdmin, SystemRoles.QualityManager]),
        new(BreachesApprove, "Privacy",
            "Approve closing a personal data breach and taking patients off its notification list, asked for by someone else (the Data Protection Officer)",
            [SystemRoles.HospitalAdmin]),
        new(RetentionManage, "Privacy", "Set how long each kind of data is kept, and run the clean-up", [SystemRoles.HospitalAdmin]),
        new(SettingsManage, "Privacy", "Set the grievance officer's contact details and the response period", [SystemRoles.HospitalAdmin]),
        new(SdfManage, "Privacy", "Keep the Significant Data Fiduciary records: the Data Protection Officer, impact assessments and data audits",
            [SystemRoles.HospitalAdmin, SystemRoles.QualityManager]),
        new(SdfApprove, "Privacy", "Approve data protection impact assessments written by someone else (the Data Protection Officer)", [SystemRoles.HospitalAdmin]),
        new(ConsentManagersManage, "Privacy", "Register the consent managers the hospital accepts patients' consent from", [SystemRoles.HospitalAdmin]),
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

    public IEnumerable<NumberSeriesDefinition> NumberSeries => PrivacyNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<PrivacySettings>();
        services.AddScoped<IHealthWarningSource, CertInContactHealthWarnings>();
        services.AddScoped<DataRequestService>();
        services.AddScoped<PersonalDataInventory>();
        services.AddScoped<ErasureService>();
        services.AddScoped<NomineeService>();
        services.AddScoped<BreachService>();
        services.AddScoped<RetentionService>();
        services.AddHostedService<RetentionJob>();

        // Erasure and backups: the ledger outside the database, replayed after a restore.
        services.AddSingleton<ErasureLedger>();
        services.AddScoped<ErasureLedgerReplay>();
        services.AddScoped<ISeeder, ErasureLedgerSeeder>();
        services.AddHostedService<ErasureLedgerJob>();

        // Children (s.9): a guardian's verifiable consent, which the consent screens check.
        services.AddScoped<GuardianConsentService>();
        services.AddScoped<IGuardianConsents>(sp => sp.GetRequiredService<GuardianConsentService>());

        // Consent managers (s.6(7)): the protocol, the real gateway and the simulator.
        services.AddScoped<ConsentManagerService>();
        services.AddHttpClient<IConsentManagerGateway, HttpConsentManagerGateway>(client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddKeyedSingleton<IConsentManagerGateway, SimulatorConsentManagerGateway>("simulator");

        // Significant Data Fiduciary (s.10): the DPO, impact assessments (approved by a second person), data audits and reminders.
        services.AddScoped<SdfService>();
        services.AddScoped<IApprovalAction, DpiaApproval>();
        services.AddScoped<IHealthWarningSource, SdfHealthWarnings>();
        services.AddHostedService<SdfReminderJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RequestEndpoints.Map(endpoints);
        BreachEndpoints.Map(endpoints);
        RetentionEndpoints.Map(endpoints);
        PrivacySettingsEndpoints.Map(endpoints);
        PortalPrivacyEndpoints.Map(endpoints);
        DpdpEndpoints.Map(endpoints);
    }
}
