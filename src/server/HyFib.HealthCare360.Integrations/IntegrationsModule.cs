using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Integrations.Features.Labels;
using HyFib.HealthCare360.Integrations.Features.Webhooks;

namespace HyFib.HealthCare360.Integrations;

public static class IntegrationsPermissions
{
    /// <summary>Build raw ZPL/TSPL for UHID cards, wristbands, sample, pharmacy and asset labels (INT-BAR).</summary>
    public const string LabelsPrint = "integrations.labels.print";

    /// <summary>
    /// Change which printer a workstation uses for labels, wristbands, receipts and documents. The printers are kept by
    /// HC360 Desktop on the workstation; the server checks this permission and records the change before it is saved.
    /// </summary>
    public const string PrintersSetup = "integrations.printers.setup";

    // Integration administration itself uses the platform's admin.integrations.manage permission.
    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(LabelsPrint, "Integrations", "Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags)", PlatformPermissions.AllStaff),
        new(PrintersSetup, "Integrations", "Choose the printers a workstation uses for labels, wristbands, receipts and documents", [SystemRoles.HospitalAdmin]),
    ];
}

/// <summary>
/// External-system adapters (SRS §5): SMS, WhatsApp, e-mail, payment gateway, POS, Tally, PACS, laboratory analysers,
/// barcode labels and ICD lookup. Each platform contract resolves to a router that picks the facility's configured
/// provider (or its Simulator) per call, so switching providers never needs a restart.
/// </summary>
public sealed class IntegrationsModule : IModule
{
    public string Name => "integrations";

    public IEnumerable<PermissionDefinition> Permissions => IntegrationsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddIntegrations();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        IntegrationAdminEndpoints.Map(endpoints);
        LabelEndpoints.Map(endpoints);
        SmsWebhookEndpoints.Map(endpoints);
        WhatsAppWebhookEndpoints.Map(endpoints);
    }
}
