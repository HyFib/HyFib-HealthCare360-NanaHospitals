using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Census;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Features.Nursing;
using HyFib.HealthCare360.Modules.Inpatient.Features.Rounds;
using HyFib.HealthCare360.Modules.Inpatient.Services;

namespace HyFib.HealthCare360.Modules.Inpatient;

public static class InpatientPermissions
{
    public const string AdmissionsRead = "inpatient.admissions.read";
    public const string Admit = "inpatient.admissions.admit";
    public const string Transfer = "inpatient.admissions.transfer";
    public const string Discharge = "inpatient.admissions.discharge";
    public const string MastersManage = "inpatient.masters.manage";
    public const string BedsManage = "beds.manage";
    public const string BedsHousekeeping = "beds.housekeeping";
    public const string NursingChart = "nursing.chart";
    public const string NursingMar = "nursing.mar";

    private static readonly string[] WardTeam = [SystemRoles.Nurse, SystemRoles.Doctor];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(AdmissionsRead, "In-patients", "View admissions, the bed board, discharges and census statistics",
            [SystemRoles.FrontOffice, SystemRoles.Nurse, SystemRoles.Doctor, SystemRoles.HospitalAdmin, SystemRoles.Billing, SystemRoles.Cashier,
             SystemRoles.InsuranceDesk, SystemRoles.Pharmacist, SystemRoles.Dietitian, SystemRoles.MrdOfficer]),
        new(Admit, "In-patients", "Admit patients, edit and cancel admissions",
            [SystemRoles.FrontOffice, SystemRoles.Nurse, SystemRoles.Doctor, SystemRoles.HospitalAdmin]),
        new(Transfer, "In-patients", "Transfer patients between beds and wards",
            [SystemRoles.FrontOffice, SystemRoles.Nurse, SystemRoles.Doctor, SystemRoles.HospitalAdmin]),
        new(Discharge, "In-patients", "Initiate, revoke and complete discharges", [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.HospitalAdmin]),
        new(MastersManage, "In-patients", "Set up wards, rooms and beds", [SystemRoles.HospitalAdmin]),
        new(BedsManage, "Beds", "Reserve and block beds, send beds for maintenance or cleaning",
            [SystemRoles.FrontOffice, SystemRoles.Nurse, SystemRoles.HospitalAdmin]),
        new(BedsHousekeeping, "Beds", "Work the housekeeping queue and release cleaned beds", [SystemRoles.Nurse, SystemRoles.HospitalAdmin]),
        new(NursingChart, "Nursing", "Nursing notes, handovers, intake/output, risk assessments and consultant visits", WardTeam),
        new(NursingMar, "Nursing", "Medication chart and medication administration record (MAR)", WardTeam),
    ];
}

/// <summary>
/// In-patient care: admission/discharge/transfer, ward and bed management with a live bed board, housekeeping,
/// occupancy and census statistics, automatic bed/nursing/consultant-visit charges, nursing documentation including the
/// medication administration record, and the doctor's ward round (SRS WB-01..06, IPB auto charges, PH-03 MAR feed, EMR-01).
/// </summary>
public sealed class InpatientModule : IModule
{
    public string Name => "inpatient";

    public IEnumerable<PermissionDefinition> Permissions => InpatientPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        InpatientRealtimeTopics.Register(services);
        services.AddScoped<AdmissionService>();
        services.AddScoped<DischargeService>();
        services.AddScoped<BedService>();
        services.AddScoped<BedBoardService>();
        services.AddScoped<CensusService>();
        services.AddScoped<InpatientChargeService>();
        services.AddScoped<MarService>();
        services.AddScoped<AdmissionAccess>();
        services.AddScoped<WardRoundQueries>();
        services.AddHostedService<BedDayChargeJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        MasterEndpoints.Map(endpoints);
        BedEndpoints.Map(endpoints);
        AdmissionEndpoints.Map(endpoints);
        DischargeEndpoints.Map(endpoints);
        CensusEndpoints.Map(endpoints);
        NursingEndpoints.Map(endpoints);
        MarEndpoints.Map(endpoints);
        RoundEndpoints.Map(endpoints);
    }
}
