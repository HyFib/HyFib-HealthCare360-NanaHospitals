using HyFib.HealthCare360.Modules.Billing.Documents;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Hbp;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Portal;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Features.Tally;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.Modules.Billing;

/// <summary>
/// Tariffs, charge capture, OP/IP billing, payments (counter, POS, payment gateway), cashier shifts, insurance/TPA,
/// claim coding, doctor fee accounting and the Tally day book (SRS MB-01..06, OPB-01..07, IPB-01..08, INS-01..07,
/// DFA-01..05, INT-PG/INT-POS/INT-TALLY). Implements the Platform billing contracts other modules depend on.
/// </summary>
public sealed class BillingModule : IModule
{
    public string Name => "billing";

    public IEnumerable<PermissionDefinition> Permissions => BillingPermissions.All;

    public IEnumerable<NumberSeriesDefinition> NumberSeries => BillingNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<TariffService>();
        services.AddScoped<ChargeService>();
        services.AddScoped<PayerCoverageService>();
        services.AddScoped<BillService>();
        services.AddScoped<DiscountService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<RefundService>();
        services.AddScoped<AdvanceService>();
        services.AddScoped<ShiftService>();
        services.AddScoped<PosService>();
        services.AddScoped<PaymentGatewayService>();
        services.AddScoped<IpBillingService>();
        services.AddScoped<PolicyService>();
        services.AddScoped<PreAuthService>();
        services.AddScoped<ClaimService>();
        services.AddScoped<DoctorFeeService>();
        services.AddScoped<TallyService>();
        services.AddScoped<EInvoiceService>();
        services.AddScoped<IEInvoiceSource, BillEInvoiceSource>();
        services.AddScoped<IEInvoiceRegister, EInvoiceRegisterLookup>();
        services.AddScoped<TallyReconciliationService>();

        // Gateway reconciliation (INT-PG): settlements, exceptions, follow-up of payments and refunds.
        services.AddScoped<TallyCorrections>();
        services.AddScoped<GatewayDiscrepancyService>();
        services.AddScoped<GatewayDiscrepancyActions>();
        services.AddScoped<GatewaySettlementService>();
        services.AddScoped<GatewayRefundTracker>();
        services.AddScoped<PrepaymentChecker>();
        services.AddScoped<GatewayReconciliationRunner>();
        services.AddScoped<PosRecheckRunner>();
        services.AddScoped<GatewayWebhookLog>();
        services.AddScoped<PosSettlementService>();
        services.AddScoped<HbpService>();
        services.AddScoped<BillSharing>();
        services.AddScoped<INotificationDocumentSource, BillDocumentSource>();

        // Replace the Platform's "billing not installed" fallbacks; ChargeService is shared so a request has one unit of work.
        services.Replace(ServiceDescriptor.Scoped<IChargePoster>(sp => sp.GetRequiredService<ChargeService>()));
        services.Replace(ServiceDescriptor.Scoped<IBillingStatusProvider, BillingStatusProvider>());
        services.Replace(ServiceDescriptor.Scoped<IPayerDirectory, PayerDirectory>());

        services.AddHostedService<TallyDayBookScheduler>();
        services.AddHostedService<GatewayReconciliationJob>();
        services.AddHostedService<PosRecheckJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        TariffEndpoints.Map(endpoints);
        PackageEndpoints.Map(endpoints);
        HbpEndpoints.Map(endpoints);
        ChargeEndpoints.Map(endpoints);
        BillEndpoints.Map(endpoints);
        DiscountEndpoints.Map(endpoints);
        PaymentEndpoints.Map(endpoints);
        GatewayEndpoints.Map(endpoints);
        ReconciliationEndpoints.Map(endpoints);
        PosSettlementEndpoints.Map(endpoints);
        ShiftEndpoints.Map(endpoints);
        IpEndpoints.Map(endpoints);
        PayerEndpoints.Map(endpoints);
        PreAuthEndpoints.Map(endpoints);
        ClaimEndpoints.Map(endpoints);
        DoctorFeeEndpoints.Map(endpoints);
        TallyEndpoints.Map(endpoints);
        EInvoiceEndpoints.Map(endpoints);
        PortalBillEndpoints.Map(endpoints);
    }
}
