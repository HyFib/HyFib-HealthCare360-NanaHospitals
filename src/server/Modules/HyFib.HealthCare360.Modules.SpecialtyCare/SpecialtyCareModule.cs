using Microsoft.Extensions.DependencyInjection.Extensions;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Mtp;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Physio;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SpecialtyCare;

/// <summary>
/// Operation theatre, dialysis, physiotherapy, blood bank and termination of pregnancy (SRS OT-01..07, DIA-01..05, PHY-01..04,
/// BB-01..07; the MTP Act).
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
        services.AddScoped<HyFib.HealthCare360.Modules.Patients.Domain.IDowntimePackSource, Features.DowntimePack.DowntimePackOtList>();
        services.AddScoped<IPractitionerWorkSource, SurgeryPractitionerWork>();
        services.AddScoped<OtClinicalService>();
        services.AddWitnessPurpose(OtClinicalService.CountWitness);
        services.AddScoped<OtReportingService>();
        services.AddScoped<DialysisService>();
        services.AddScoped<PhysioService>();
        services.AddScoped<DonorService>();
        services.AddScoped<TransfusionService>();

        // Quality's indicators (return to OT, SSI and transfusion reaction rates) and infection surveillance read these facts.
        services.Replace(ServiceDescriptor.Scoped<ISurgeryAndTransfusionFacts, SurgeryAndTransfusionFacts>());
        services.AddScoped<BedsideTransfusionService>();
        services.AddWitnessPurpose(BedsideTransfusionService.BedsideWitness);
        services.AddWitnessPurpose(MtpService.SecondOpinionWitness);
        services.AddScoped<BloodInventoryService>();
        services.AddScoped<MtpService>();

        // A patient in pre-op or on the table holds the discharge (Inpatient asks every source).
        services.AddScoped<IDischargeHoldSource, TheatreDischargeHolds>();
        services.AddScoped<IDischargeHoldSource, TransfusionDischargeHolds>();

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
        BedsideEndpoints.Map(endpoints);
        MtpEndpoints.Map(endpoints);
    }
}
