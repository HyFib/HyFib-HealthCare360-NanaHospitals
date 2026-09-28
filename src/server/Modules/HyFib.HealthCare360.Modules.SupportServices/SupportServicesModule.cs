using HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Services;

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

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(CssdRead, "CSSD", "View instrument sets, loads, packs, issues and CSSD reports",
            [SystemRoles.CssdTechnician, SystemRoles.Nurse, SystemRoles.OtCoordinator, SystemRoles.QualityManager, SystemRoles.HospitalAdmin]),
        new(CssdRequests, "CSSD", "Request sterile sets for a department and record packs used on patients",
            [SystemRoles.Nurse, SystemRoles.OtCoordinator, SystemRoles.CssdTechnician, SystemRoles.HospitalAdmin]),
        new(CssdProcess, "CSSD", "Receive soiled trays, run sterilizer loads and issue sterile packs", [SystemRoles.CssdTechnician, SystemRoles.HospitalAdmin]),
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
    ];
}

/// <summary>
/// Support services: CSSD with sterilizer loads, issue with expiry and recall by load (CSSD-01..05); dietary orders,
/// ward-wise meal plans, kitchen production and delivery tracking with optional diet charges (DIET-01..05); and the
/// canteen POS with GST, staff credit and payroll deduction (CAN-01..04).
/// </summary>
public sealed class SupportServicesModule : IModule
{
    public string Name => "supportservices";

    public IEnumerable<PermissionDefinition> Permissions => SupportServicesPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SterilizationService>();
        services.AddScoped<CssdService>();
        services.AddScoped<DietOrderService>();
        services.AddScoped<MealPlanService>();
        services.AddScoped<CanteenService>();
        services.AddScoped<StaffCreditService>();
        services.AddHostedService<DailyMealPlanJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        CssdEndpoints.Map(endpoints);
        SterilizationEndpoints.Map(endpoints);
        DietaryEndpoints.Map(endpoints);
        MealPlanEndpoints.Map(endpoints);
        CanteenEndpoints.Map(endpoints);
        StaffCreditEndpoints.Map(endpoints);
    }
}
