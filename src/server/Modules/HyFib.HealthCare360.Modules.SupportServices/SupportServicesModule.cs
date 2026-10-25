using HyFib.HealthCare360.Modules.SupportServices.Features.Ambulance;
using HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Mortuary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Waste;
using HyFib.HealthCare360.Modules.SupportServices.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupportServices;

public static class SupportServicesPermissions
{
    public const string CssdRead = "cssd.read";
    public const string CssdRequests = "cssd.requests";
    public const string CssdProcess = "cssd.process";
    public const string CssdRelease = "cssd.release";
    public const string CssdRecalls = "cssd.recalls";
    public const string CssdMasters = "cssd.masters.manage";

    public const string DietOrdersRead = "dietary.orders.read";
    public const string DietOrdersManage = "dietary.orders.manage";
    public const string DietMeals = "dietary.meals.manage";
    public const string DietMasters = "dietary.masters.manage";

    public const string CanteenSales = "canteen.sales.create";
    public const string CanteenCancel = "canteen.sales.cancel";
    public const string CanteenMenu = "canteen.menu.manage";
    public const string CanteenReports = "canteen.reports.read";
    public const string CanteenCredit = "canteen.credit.manage";

    /// <summary>Weigh and bar-code bags of bio-medical waste where it is generated.</summary>
    public const string WasteRecord = "bmw.record";

    /// <summary>Hand bags over to the treatment facility, and keep the register and the annual report (Form IV).</summary>
    public const string WasteManage = "bmw.manage";

    /// <summary>Receive bodies into the mortuary, release them, and keep the register.</summary>
    public const string MortuaryManage = "mortuary.manage";

    /// <summary>Set the mortuary's cold-storage slots.</summary>
    public const string MortuaryConfigure = "mortuary.configure";

    /// <summary>Take ambulance calls, send vehicles and record each trip.</summary>
    public const string AmbulanceDispatch = "ambulance.dispatch";

    /// <summary>Keep the ambulance fleet and its certificates.</summary>
    public const string AmbulanceFleet = "ambulance.fleet";

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(CssdRead, "CSSD", "View instrument sets, loads, packs, issues and CSSD reports",
            [SystemRoles.CssdTechnician, SystemRoles.Nurse, SystemRoles.OtCoordinator, SystemRoles.QualityManager, SystemRoles.HospitalAdmin]),
        new(CssdRequests, "CSSD", "Request sterile sets for a department and record packs used on patients",
            [SystemRoles.Nurse, SystemRoles.OtCoordinator, SystemRoles.CssdTechnician, SystemRoles.HospitalAdmin]),
        new(CssdProcess, "CSSD", "Receive soiled trays, run wash cycles and sterilizer loads, and issue sterile packs", [SystemRoles.CssdTechnician, SystemRoles.HospitalAdmin]),
        new(CssdRelease, "CSSD", "Release loads and record biological indicator results (triggers recalls)", [SystemRoles.CssdTechnician, SystemRoles.HospitalAdmin]),
        new(CssdRecalls, "CSSD", "Receive CSSD recall and count-discrepancy alerts and acknowledge recalled packs",
            [SystemRoles.CssdTechnician, SystemRoles.OtCoordinator, SystemRoles.Nurse, SystemRoles.QualityManager, SystemRoles.HospitalAdmin]),
        new(CssdMasters, "CSSD", "Maintain instrument sets and count sheets", [SystemRoles.CssdTechnician, SystemRoles.HospitalAdmin]),

        new(DietOrdersRead, "Dietary", "View diet orders and ward meal plans",
            [SystemRoles.Dietitian, SystemRoles.Nurse, SystemRoles.Doctor, SystemRoles.CanteenManager, SystemRoles.HospitalAdmin, SystemRoles.Billing]),
        new(DietOrdersManage, "Dietary", "Write, stop and review diet orders", [SystemRoles.Doctor, SystemRoles.Dietitian, SystemRoles.Nurse]),
        new(DietMeals, "Dietary", "Generate meal plans, view kitchen production lists and record meal deliveries",
            [SystemRoles.Dietitian, SystemRoles.CanteenManager, SystemRoles.Nurse, SystemRoles.HospitalAdmin]),
        new(DietMasters, "Dietary", "Maintain the diet master", [SystemRoles.Dietitian, SystemRoles.HospitalAdmin]),

        new(CanteenSales, "Canteen", "Bill canteen sales (POS) and take staff credit repayments", [SystemRoles.CanteenManager]),
        new(CanteenCancel, "Canteen", "Cancel today's canteen bills", [SystemRoles.CanteenManager, SystemRoles.HospitalAdmin]),
        new(CanteenMenu, "Canteen", "Maintain the canteen menu, prices and stock", [SystemRoles.CanteenManager, SystemRoles.HospitalAdmin]),
        new(CanteenReports, "Canteen", "View canteen sales reports", [SystemRoles.CanteenManager, SystemRoles.HospitalAdmin, SystemRoles.Accountant]),
        new(CanteenCredit, "Canteen", "Staff credit balances, statements, payroll export and settlement",
            [SystemRoles.CanteenManager, SystemRoles.HrManager, SystemRoles.HospitalAdmin]),

        new(WasteRecord, "Bio-medical waste", "Weigh and bar-code bags of bio-medical waste where it is generated",
            [SystemRoles.Nurse, SystemRoles.LabTechnician, SystemRoles.CssdTechnician, SystemRoles.OtCoordinator, SystemRoles.QualityManager, SystemRoles.HospitalAdmin]),
        new(WasteManage, "Bio-medical waste", "Hand waste over to the treatment facility, the register and the annual report (Form IV)",
            [SystemRoles.QualityManager, SystemRoles.HospitalAdmin]),

        new(MortuaryManage, "Mortuary", "Receive bodies into the mortuary, release them and keep the register",
            [SystemRoles.Nurse, SystemRoles.HospitalAdmin]),
        new(MortuaryConfigure, "Mortuary", "Set the mortuary's cold-storage slots", [SystemRoles.HospitalAdmin]),

        new(AmbulanceDispatch, "Ambulance", "Take ambulance calls, send vehicles and record each trip",
            [SystemRoles.FrontOffice, SystemRoles.Nurse, SystemRoles.HospitalAdmin]),
        new(AmbulanceFleet, "Ambulance", "Keep the ambulance fleet and its certificates", [SystemRoles.HospitalAdmin]),
    ];
}

/// <summary>
/// Support services: CSSD with sterilizer loads, issue with expiry and recall by load (CSSD-01..05); dietary orders,
/// ward-wise meal plans, kitchen production and delivery tracking with optional diet charges (DIET-01..05); and the
/// canteen POS with GST, staff credit and payroll deduction (CAN-01..04); bio-medical waste; the mortuary; and the ambulance fleet.
/// </summary>
public sealed class SupportServicesModule : IModule
{
    public string Name => "supportservices";

    public IEnumerable<PermissionDefinition> Permissions => SupportServicesPermissions.All;

    public IEnumerable<NumberSeriesDefinition> NumberSeries => SupportServicesNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SterilizationService>();
        services.AddScoped<WashCycleService>();
        services.AddScoped<CssdService>();
        services.AddScoped<DietOrderService>();
        services.AddScoped<MealPlanService>();
        services.AddScoped<CanteenService>();
        services.AddScoped<StaffCreditService>();
        services.AddScoped<WasteService>();
        services.AddScoped<MortuaryService>();
        services.AddScoped<AmbulanceService>();

        // The canteen's day for Billing's Tally day book (Billing reads it without referencing this module).
        services.AddScoped<ICounterSalesSource, CanteenDayBookSource>();
        services.AddHostedService<DailyMealPlanJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        CssdEndpoints.Map(endpoints);
        SterilizationEndpoints.Map(endpoints);
        WashCycleEndpoints.Map(endpoints);
        DietaryEndpoints.Map(endpoints);
        MealPlanEndpoints.Map(endpoints);
        CanteenEndpoints.Map(endpoints);
        StaffCreditEndpoints.Map(endpoints);
        WasteEndpoints.Map(endpoints);
        MortuaryEndpoints.Map(endpoints);
        AmbulanceEndpoints.Map(endpoints);
    }
}
