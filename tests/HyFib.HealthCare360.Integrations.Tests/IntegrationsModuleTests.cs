using HyFib.HealthCare360.Integrations.Email;
using HyFib.HealthCare360.Integrations.Icd;
using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Integrations.Pacs.Worklist;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Integrations.Pos;
using HyFib.HealthCare360.Integrations.Sms;
using HyFib.HealthCare360.Integrations.Tally;
using HyFib.HealthCare360.Integrations.WhatsApp;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.Integrations.Tests;

public sealed class IntegrationsModuleTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        new IntegrationsModule().AddServices(services, new ConfigurationBuilder().Build());
        return services;
    }

    [Fact]
    public void Module_key_is_integrations() => new IntegrationsModule().Name.ShouldBe("integrations");

    [Fact]
    public void Declares_the_label_printing_permission() =>
        new IntegrationsModule().Permissions.Select(p => p.Name).ShouldBe([IntegrationsPermissions.LabelsPrint]);

    [Theory]
    [InlineData(typeof(ISmsSender), typeof(SmsRouter))]
    [InlineData(typeof(IWhatsAppSender), typeof(WhatsAppRouter))]
    [InlineData(typeof(IEmailSender), typeof(EmailRouter))]
    [InlineData(typeof(IPaymentGateway), typeof(PaymentGatewayRouter))]
    [InlineData(typeof(IPosTerminal), typeof(PosTerminalRouter))]
    [InlineData(typeof(IAccountingExporter), typeof(AccountingExporterRouter))]
    [InlineData(typeof(IPacsClient), typeof(PacsClientRouter))]
    [InlineData(typeof(IIcdLookup), typeof(IcdLookupRouter))]
    [InlineData(typeof(ILabelPrinterService), typeof(LabelPrinterService))]
    public void Every_platform_contract_resolves_to_its_router(Type contract, Type implementation) =>
        Services().Where(d => d.ServiceType == contract).ShouldHaveSingleItem().ImplementationType.ShouldBe(implementation);

    [Fact]
    public void Platform_fallbacks_added_later_do_not_replace_the_routers()
    {
        var services = Services();

        // The platform registers its "not configured" channel fallbacks with TryAdd after the modules.
        services.TryAddScoped<IPaymentGateway, PaymentGatewayRouter>();
        services.TryAddScoped<ISmsSender, SmsRouter>();

        services.Count(d => d.ServiceType == typeof(IPaymentGateway)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(ISmsSender)).ShouldBe(1);
    }

    [Fact]
    public void Runs_the_laboratory_gateway_as_a_hosted_service() =>
        Services().ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(LabGatewayHostedService));

    [Fact]
    public void Runs_the_modality_worklist_server_as_a_hosted_service_sharing_one_gateway()
    {
        var services = Services();

        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ModalityWorklistHostedService));
        services.ShouldContain(d => d.ServiceType == typeof(ModalityWorklistGateway) && d.Lifetime == ServiceLifetime.Singleton);
        services.ShouldContain(d => d.ServiceType == typeof(ModalityWorklistState) && d.Lifetime == ServiceLifetime.Singleton);

        // The worklist itself comes from Diagnostics through the platform contract, never from this module.
        services.ShouldNotContain(d => d.ServiceType == typeof(IModalityWorklistSource));
    }
}
