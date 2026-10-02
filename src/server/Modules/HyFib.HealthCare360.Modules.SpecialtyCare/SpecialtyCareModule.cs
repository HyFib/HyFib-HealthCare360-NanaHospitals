using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Physio;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SpecialtyCare;

/// <summary>
/// Operation theatre, dialysis, physiotherapy and blood bank (SRS OT-01..07, DIA-01..05, PHY-01..04, BB-01..07).
/// Order and admission event handlers, validators and the seeder are discovered automatically from this assembly.
/// </summary>
public sealed class SpecialtyCareModule : IModule
{
    public string Name => "specialtycare";

    public IEnumerable<PermissionDefinition> Permissions => SpecialtyCarePermissions.All;

    public IEnumerable<NumberSeriesDefinition> NumberSeries => SpecialtyCareNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        SpecialtyCareRealtimeTopics.Register(services);
        services.AddScoped<SpecialtyChargeService>();
        services.AddScoped<SurgeryService>();
        services.AddScoped<OtClinicalService>();
        services.AddScoped<OtReportingService>();
        services.AddScoped<DialysisService>();
        services.AddScoped<PhysioService>();
        services.AddScoped<DonorService>();
        services.AddScoped<TransfusionService>();
        services.AddScoped<BloodInventoryService>();

        // Daily expiry housekeeping; it checks PlatformOptions.RunsJobs itself so API-only nodes stay idle.
        services.AddHostedService<BloodUnitExpiryJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        TheatreEndpoints.Map(endpoints);
        SurgeryEndpoints.Map(endpoints);
        OtClinicalEndpoints.Map(endpoints);
        OtStoreEndpoints.Map(endpoints);
        DialysisSetupEndpoints.Map(endpoints);
        DialysisSessionEndpoints.Map(endpoints);
        PhysioEndpoints.Map(endpoints);
        DonorEndpoints.Map(endpoints);
        InventoryEndpoints.Map(endpoints);
        TransfusionEndpoints.Map(endpoints);
    }
}
