using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Integrations.Features.LabConnections;
using HyFib.HealthCare360.Integrations.Features.Labels;
using HyFib.HealthCare360.Integrations.Features.Webhooks;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;

namespace HyFib.HealthCare360.Integrations;

public static class IntegrationsPermissions
{
    /// <summary>Build raw ZPL/TSPL for UHID cards, wristbands, sample, pharmacy and asset labels (INT-BAR).</summary>
    public const string LabelsPrint = "integrations.labels.print";

    /// <summary>Set up how analysers connect: network ports and allowed addresses, or serial ports through a lab agent (INT-LAB).</summary>
    public const string LabConnectionsManage = "integrations.lab.connections";

    /// <summary>See whether analysers and their lab agents are connected, and orders waiting for them.</summary>
    public const string LabStatusRead = "integrations.lab.status";

    /// <summary>
    /// Change which printer a workstation uses for labels, wristbands, receipts and documents, and its serial barcode
    /// scanner. Both are kept by HC360 Desktop on the workstation; the server checks this permission before a change is
    /// saved and records it after.
    /// </summary>
    public const string PrintersSetup = "integrations.printers.setup";

    // Integration administration itself uses the platform's admin.integrations.manage permission.
    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(LabelsPrint, "Integrations", "Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags)", PlatformPermissions.AllStaff),
        new(LabConnectionsManage, "Integrations", "Set up analyser connections: network ports, allowed addresses, serial ports and lab agents", [SystemRoles.HospitalAdmin]),
        new(LabStatusRead, "Integrations", "See whether lab analysers and their agents are connected", [SystemRoles.LabTechnician, SystemRoles.Pathologist, SystemRoles.HospitalAdmin]),
        new(PrintersSetup, "Integrations", "Choose the printers a workstation uses for labels, wristbands, receipts and documents, and its serial barcode scanner", [SystemRoles.HospitalAdmin]),
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
        LabConnectionEndpoints.Map(endpoints);
        LabAgentEndpoints.Map(endpoints);
    }
}
