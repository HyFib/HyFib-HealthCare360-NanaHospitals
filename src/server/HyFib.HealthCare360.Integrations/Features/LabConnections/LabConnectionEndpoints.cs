namespace HyFib.HealthCare360.Integrations.Features.LabConnections;

/// <summary>
/// <c>/api/v1/integrations/lab/connections</c>: how each analyser reaches the gateway, for Lab setup › Analysers.
/// Lab staff with <c>integrations.lab.status</c> see whether analysers and their agents are connected; setting up a
/// connection (ports, allowed addresses, serial ports and agents) needs <c>integrations.lab.connections</c>.
/// </summary>
internal static class LabConnectionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations/lab/connections").WithTags("Laboratory analysers");
        group.MapGet("/", List)
            .RequireAnyPermission(IntegrationsPermissions.LabConnectionsManage, IntegrationsPermissions.LabStatusRead, PlatformPermissions.IntegrationsManage);
        group.MapPut("/{code}", Save)
            .RequirePermission(IntegrationsPermissions.LabConnectionsManage)
            .WithValidation<SaveLabConnectionRequest>();
        group.MapDelete("/{code}", Remove)
            .RequirePermission(IntegrationsPermissions.LabConnectionsManage);
    }

    private static async Task<IResult> List(LabConnectionService connections, CancellationToken ct) =>
        TypedResults.Ok(await connections.ListAsync(ct));

    private static async Task<IResult> Save(string code, SaveLabConnectionRequest request, LabConnectionService connections, CancellationToken ct) =>
        (await connections.SaveAsync(code.Trim(), request, ct)).ToNoContent();

    private static async Task<IResult> Remove(string code, uint? version, LabConnectionService connections, CancellationToken ct) =>
        (await connections.RemoveAsync(code.Trim(), version ?? 0, ct)).ToNoContent();
}
