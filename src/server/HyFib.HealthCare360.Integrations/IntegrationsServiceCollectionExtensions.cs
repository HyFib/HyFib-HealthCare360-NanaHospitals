using HyFib.HealthCare360.Integrations.Email;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Integrations.Features.Webhooks;
using HyFib.HealthCare360.Integrations.Icd;
using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;
using HyFib.HealthCare360.Integrations.Features.LabConnections;
using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Integrations.Pacs.Worklist;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Integrations.Pdf;
using HyFib.HealthCare360.Integrations.Pos;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Integrations.Simulator;
using HyFib.HealthCare360.Integrations.Sms;
using HyFib.HealthCare360.Integrations.Tally;
using HyFib.HealthCare360.Integrations.WhatsApp;

namespace HyFib.HealthCare360.Integrations;

/// <summary>
/// Registers every adapter. Platform contracts are added with plain <c>Add*</c>: the platform registers its
/// "unconfigured" channel fallbacks with <c>TryAdd*</c> after the modules, so these win. Each contract resolves to a
/// router that picks the facility's configured provider on every call.
/// </summary>
internal static class IntegrationsServiceCollectionExtensions
{
    // Cloud APIs answer in seconds, and the outbox gives a delivery handler 30 s in total, so calls give up well before.
    private static readonly TimeSpan CloudAttempt = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloudTotal = TimeSpan.FromSeconds(20);

    // Tally imports large voucher batches slowly on the accounts PC, but a post attempt runs as an outbox message with
    // 30 s in all, and stops calling Tally after 24 s (TallyService.CallDeadline): one call must fit well inside that.
    private static readonly TimeSpan TallyTimeout = TimeSpan.FromSeconds(20);

    public static IServiceCollection AddIntegrations(this IServiceCollection services)
    {
        services.AddScoped<IntegrationSettingsReader>();
        services.AddScoped<IntegrationTrace>();
        services.AddScoped<SimulatorInbox>();

        AddMessaging(services);
        AddPayments(services);
        AddAccountingAndImaging(services);
        AddLaboratory(services);

        services.AddSingleton<ILabelPrinterService, LabelPrinterService>();
        services.AddSingleton<IPdfRenderer, EdgePdfRenderer>();
        services.AddScoped<NotificationLogUpdater>();
        services.AddScoped<WhatsAppInboundStore>();
        services.AddScoped<IntegrationStatusService>();
        services.AddScoped<IntegrationTestService>();
        return services;
    }

    private static void AddMessaging(IServiceCollection services)
    {
        services.AddIntegrationHttpClient<Msg91SmsProvider>(CloudAttempt, CloudTotal);
        services.AddIntegrationHttpClient<TwilioSmsProvider>(CloudAttempt, CloudTotal);
        services.AddIntegrationHttpClient<GenericHttpSmsProvider>(CloudAttempt, CloudTotal);
        services.AddScoped<ISmsProvider>(sp => sp.GetRequiredService<Msg91SmsProvider>());
        services.AddScoped<ISmsProvider>(sp => sp.GetRequiredService<TwilioSmsProvider>());
        services.AddScoped<ISmsProvider>(sp => sp.GetRequiredService<GenericHttpSmsProvider>());
        services.AddScoped<ISmsProvider, SimulatorSmsProvider>();
        services.AddScoped<ISmsTemplateCatalog, SmsTemplateCatalog>();
        services.AddScoped<ISmsSender, SmsRouter>();

        services.AddIntegrationHttpClient<MetaWhatsAppProvider>(CloudAttempt, CloudTotal);
        services.AddScoped<IWhatsAppProvider>(sp => sp.GetRequiredService<MetaWhatsAppProvider>());
        services.AddScoped<IWhatsAppProvider, SimulatorWhatsAppProvider>();
        services.AddScoped<IWhatsAppSender, WhatsAppRouter>();

        services.AddScoped<IEmailProvider, SmtpEmailProvider>();
        services.AddScoped<IEmailProvider, SimulatorEmailProvider>();
        services.AddScoped<IEmailSender, EmailRouter>();
    }

    private static void AddPayments(IServiceCollection services)
    {
        services.AddSingleton<SimulatorSigningKey>();
        services.AddIntegrationHttpClient<RazorpayGateway>(CloudAttempt, CloudTotal);
        services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<RazorpayGateway>());
        services.AddScoped<IPaymentProvider, SimulatorPaymentGateway>();
        services.AddScoped<IPaymentGateway, PaymentGatewayRouter>();

        services.AddIntegrationHttpClient<PineLabsPosTerminal>(CloudAttempt, CloudTotal);
        services.AddScoped<IPosProvider>(sp => sp.GetRequiredService<PineLabsPosTerminal>());
        services.AddScoped<IPosProvider, SimulatorPosTerminal>();
        services.AddScoped<IPosTerminal, PosTerminalRouter>();
    }

    private static void AddAccountingAndImaging(IServiceCollection services)
    {
        services.AddIntegrationHttpClient<TallyPrimeExporter>(TallyTimeout, TallyTimeout);
        services.AddScoped<IAccountingProvider>(sp => sp.GetRequiredService<TallyPrimeExporter>());
        services.AddScoped<SimulatedTallyCompany>();
        services.AddScoped<IAccountingProvider, SimulatorAccountingExporter>();
        services.AddScoped<IAccountingExporter, AccountingExporterRouter>();

        services.AddIntegrationHttpClient<DicomWebPacsProvider>(CloudAttempt, CloudTotal);
        services.AddScoped<IPacsProvider>(sp => sp.GetRequiredService<DicomWebPacsProvider>());
        services.AddScoped<IPacsProvider, SimulatorPacsProvider>();
        services.AddScoped<IPacsClient, PacsClientRouter>();
        services.AddModalityWorklist();

        services.AddSingleton<IcdTokenCache>();
        services.AddIntegrationHttpClient<WhoIcdApiClient>(CloudAttempt, CloudTotal);
        services.AddScoped<IIcdLookup, IcdLookupRouter>();
    }

    private static void AddLaboratory(IServiceCollection services)
    {
        services.AddSingleton<LabGatewayState>();
        services.AddSingleton<ILabMessageProcessor, LabMessageProcessor>();
        services.AddHostedService<LabGatewayHostedService>();

        // Orders sent to analysers unasked, serial analysers relayed by lab agents, and their set-up in Lab setup.
        services.AddScoped<LabOrderQueue>();
        services.AddSingleton<LabRelayRegistry>();
        services.AddScoped<LabAgentAuthenticator>();
        services.AddScoped<LabConnectionService>();
    }
}
