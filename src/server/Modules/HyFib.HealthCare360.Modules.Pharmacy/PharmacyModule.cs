using HyFib.HealthCare360.Modules.Pharmacy.Features.Dashboard;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Ndps;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Refills;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Registers;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Pharmacy;

public static class PharmacyPermissions
{
    public const string QueueRead = "pharmacy.queue.read";
    public const string Dispense = "pharmacy.dispense";
    public const string Sales = "pharmacy.sales";
    public const string Returns = "pharmacy.returns";
    public const string IpIssue = "pharmacy.ip.issue";

    /// <summary>Nurses raise ward indents for their admitted patients.</summary>
    public const string IpIndent = "pharmacy.ip.indent";

    public const string Registers = "pharmacy.registers";
    public const string Refills = "pharmacy.refills";

    /// <summary>Count the essential narcotic drugs at a change of shift, and witness another's count (NDPS).</summary>
    public const string NdpsCount = "pharmacy.ndps.count";

    /// <summary>Anyone working a pharmacy counter (stock pickers, queue, batch lists).</summary>
    internal static readonly string[] CounterStaff = [QueueRead, Dispense, Sales, IpIssue];

    private static readonly string[] Pharmacy = [SystemRoles.Pharmacist, SystemRoles.HospitalAdmin];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(QueueRead, "Pharmacy", "View the e-prescription queue and pharmacy stock", Pharmacy),
        new(Dispense, "Pharmacy", "Dispense prescriptions (FEFO batches, substitution, counselling)", Pharmacy),
        new(Sales, "Pharmacy", "Counter sales, GST invoices and collection of dues", Pharmacy),
        new(Returns, "Pharmacy", "Sales returns and same-day invoice cancellation", Pharmacy),
        new(IpIssue, "Pharmacy", "Issue medicines to admitted patients and accept ward returns", Pharmacy),
        new(IpIndent, "Pharmacy", "Raise ward indents for admitted patients", [.. Pharmacy, SystemRoles.Nurse]),
        new(Registers, "Pharmacy", "View and export the Schedule H/H1/X and narcotic registers", Pharmacy),
        new(Refills, "Pharmacy", "Authorise, approve and reject prescription refills", Pharmacy),
        new(NdpsCount, "Pharmacy", "Count the narcotic drugs at a change of shift, and witness another's count",
            [SystemRoles.Pharmacist, SystemRoles.Nurse]),
    ];
}

/// <summary>OP and IP pharmacy: e-prescription queue, dispensing, counter sales, returns, refills and controlled-drug registers (SRS PH-01..06).</summary>
public sealed class PharmacyModule : IModule
{
    public string Name => "pharmacy";

    public IEnumerable<PermissionDefinition> Permissions => PharmacyPermissions.All;

    public IEnumerable<NumberSeriesDefinition> NumberSeries => PharmacyNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        PharmacyRealtimeTopics.Register(services);
        services.AddScoped<DispensingService>();
        services.AddScoped<SaleAdjustmentService>();
        services.AddScoped<InpatientPharmacyService>();
        services.AddScoped<ControlledDrugRegisterService>();
        services.AddScoped<RefillService>();
        services.AddScoped<NdpsService>();
        services.AddScoped<PharmacyRealtime>();

        // The pharmacy counter's day for Billing's Tally day book (Billing reads it without referencing this module).
        services.AddScoped<ICounterSalesSource, PharmacyDayBookSource>();
        services.AddHostedService<RefillReminderJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        QueueEndpoints.Map(endpoints);
        SaleEndpoints.Map(endpoints);
        InpatientEndpoints.Map(endpoints);
        RegisterEndpoints.Map(endpoints);
        NdpsEndpoints.Map(endpoints);
        RefillEndpoints.Map(endpoints);
        DashboardEndpoints.Map(endpoints);
    }
}
