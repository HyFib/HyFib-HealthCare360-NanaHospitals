using HyFib.HealthCare360.Modules.Quality.Features.Audits;
using HyFib.HealthCare360.Modules.Quality.Features.Capas;
using HyFib.HealthCare360.Modules.Quality.Features.Dashboard;
using HyFib.HealthCare360.Modules.Quality.Features.Downtime;
using HyFib.HealthCare360.Modules.Quality.Features.Hai;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Modules.Quality.Features.Indicators;
using HyFib.HealthCare360.Modules.Quality.Features.Mvpi;
using HyFib.HealthCare360.Modules.Quality.Features.Risks;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Modules.Quality.Services.Indicators;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HyFib.HealthCare360.Modules.Quality;

public static class QualityPermissions
{
    /// <summary>Every staff member may report incidents (and progress CAPAs they own).</summary>
    public const string IncidentsReport = "quality.incidents.report";

    /// <summary>
    /// The incident register: review, grade and investigate incidents, and report adverse events with medical devices to
    /// the Materiovigilance Programme of India (MvPI) from them.
    /// </summary>
    public const string IncidentsManage = "quality.incidents.manage";

    /// <summary>
    /// Approve closing a sentinel or never event, and re-grading one so it is no longer sentinel, asked for by someone else
    /// (MAKER-CHECKER.md, slice 14): the quality manager. Never the incident's reporter.
    /// </summary>
    public const string IncidentsApprove = "quality.incidents.approve";

    public const string CapaManage = "quality.capa.manage";
    public const string RisksManage = "quality.risks.manage";
    public const string IndicatorsManage = "quality.indicators.manage";

    /// <summary>Record spells of working without HC360 (and drills of it), and sign off their back-entry.</summary>
    public const string DowntimeManage = "quality.downtime.manage";

    /// <summary>Infection surveillance: review possible hospital-acquired infections against the NHSN definitions, see the rates; receives culture alerts.</summary>
    public const string HaiManage = "quality.hai.manage";

    /// <summary>Record invasive devices (central lines, urinary catheters, ventilation) put in and taken out: the source of device days.</summary>
    public const string HaiDevices = "quality.hai.devices";

    /// <summary>NABH internal audits: checklists, the schedule, findings and closure.</summary>
    public const string AuditsManage = "quality.audits.manage";

    private static readonly string[] Quality = [SystemRoles.QualityManager, SystemRoles.HospitalAdmin];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(IncidentsReport, "Quality", "Report incidents and near misses (optionally anonymously); work on own corrective actions", PlatformPermissions.AllStaff),
        new(IncidentsManage, "Quality", "Review, grade and investigate incidents; receive sentinel-event alerts; report device adverse events to MvPI", Quality),
        new(IncidentsApprove, "Quality",
            "Approve closing a sentinel or never event, and re-grading one out of sentinel, asked for by someone else (never one's own report)", Quality),
        new(CapaManage, "Quality", "Plan corrective/preventive actions and verify their effectiveness", Quality),
        new(RisksManage, "Quality", "Maintain the risk register", Quality),
        new(IndicatorsManage, "Quality", "Define quality indicators and record their values", Quality),
        new(DowntimeManage, "Quality", "Record downtime of HC360 and drills of the downtime procedure, and sign off the back-entry of paper records", Quality),
        new(HaiManage, "Quality", "Review possible hospital-acquired infections against the NHSN definitions and see the HAI rates; receive positive-culture alerts", Quality),
        new(HaiDevices, "Quality", "Record central lines, urinary catheters and ventilation put in and taken out (device days for infection surveillance)",
            [SystemRoles.Nurse, SystemRoles.Doctor, SystemRoles.QualityManager, SystemRoles.HospitalAdmin]),
        new(AuditsManage, "Quality", "Keep the NABH audit checklists, schedule internal audits and close them", Quality),
    ];
}

/// <summary>
/// IRMS: incident reporting, root-cause analysis, CAPA, risk register and NABH quality indicators (SRS IRMS-01..06), and
/// device adverse event reports to the Materiovigilance Programme of India.
/// </summary>
public sealed class QualityModule : IModule
{
    public string Name => "quality";

    public IEnumerable<PermissionDefinition> Permissions => QualityPermissions.All;

    public IEnumerable<NumberSeriesDefinition> NumberSeries => QualityNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IncidentService>();

        // Departments that cannot reference Quality raise their never events into the register (SpecialtyCare's theatre).
        services.Replace(ServiceDescriptor.Scoped<IIncidentRegister, IncidentRegister>());
        services.AddScoped<CapaService>();
        services.AddScoped<ICapaRegister, CapaRegister>();
        services.AddScoped<AnonymousReportingScope>();
        services.AddScoped<MvpiService>();
        services.AddScoped<MvpiAccess>();

        // Computed NABH indicators, HAI surveillance and internal audits.
        services.AddScoped<IndicatorMeasures>();
        services.AddScoped<IndicatorComputation>();
        services.AddHostedService<IndicatorComputationJob>();
        services.AddScoped<HaiService>();
        services.AddHostedService<CultureAlertJob>();
        services.AddScoped<AuditService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        IncidentEndpoints.Map(endpoints);
        InvestigationEndpoints.Map(endpoints);
        CapaEndpoints.Map(endpoints);
        RiskEndpoints.Map(endpoints);
        IndicatorEndpoints.Map(endpoints);
        QualityDashboardEndpoints.Map(endpoints);
        DowntimeEndpoints.Map(endpoints);
        MvpiEndpoints.Map(endpoints);
        ComputedIndicatorEndpoints.Map(endpoints);
        HaiEndpoints.Map(endpoints);
        AuditEndpoints.Map(endpoints);
    }
}
