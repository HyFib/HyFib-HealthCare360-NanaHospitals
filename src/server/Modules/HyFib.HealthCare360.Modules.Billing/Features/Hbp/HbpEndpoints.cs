using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using Microsoft.AspNetCore.Mvc;

namespace HyFib.HealthCare360.Modules.Billing.Features.Hbp;

/// <summary>
/// /api/v1/billing/hbp — AB PM-JAY's Health Benefit Package master: imported from NHA's file by whoever keeps the
/// tariffs and put in force by a second person who may approve prices (maker-checker), and looked up by them and by the
/// insurance desk (pre-authorisations and claims to the scheme).
/// </summary>
internal static class HbpEndpoints
{
    private static readonly string[] Readers =
        [BillingPermissions.TariffsManage, BillingPermissions.PreAuthManage, BillingPermissions.ClaimsManage];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/hbp").WithTags("Billing - PM-JAY HBP");
        group.MapGet("/procedures", Search).RequireAnyPermission(Readers);
        group.MapGet("/procedures/{code}", Get).RequireAnyPermission(Readers);
        group.MapGet("/imports", Imports).RequireAnyPermission(Readers);

        // Bearer-token API (no cookie authentication), so antiforgery does not apply. A file larger than a master is
        // refused before it is read.
        group.MapPost("/imports", Import).RequirePermission(BillingPermissions.TariffsManage)
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(HbpMasterFile.MaxBytes + (64 * 1024)));
    }

    private static async Task<IResult> Search(string? search, string? specialty, bool? includeRetired, HbpService service, CancellationToken ct) =>
        TypedResults.Ok(await service.SearchAsync(search, specialty, includeRetired == true, ct));

    private static async Task<IResult> Imports(HbpService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ImportsAsync(ct));

    private static async Task<IResult> Get(string code, HbpService service, CancellationToken ct) =>
        await service.GetAsync(code, ct) is { } procedure
            ? TypedResults.Ok(procedure)
            : Error.NotFound("billing.hbp_not_found", $"{code} is not in the imported HBP master.").ToProblem();

    /// <summary>
    /// Multipart <c>file</c> (NHA's HBP master, .xlsx or CSV, at most 10 MB), <c>version</c> (its name, for example
    /// "HBP 2022") and <c>reason</c> (for the approver, for example the circular that issued it). Nothing changes in the
    /// master yet: 202 with the import staged (what it would change, and the rows not read) and the approval it waits
    /// for; 400 <c>file</c>, <c>version</c> or <c>reason</c>; 409 while another import waits; 422 <c>hbp.unchanged</c>
    /// when the master already reads as the file does.
    /// </summary>
    private static async Task<IResult> Import(IFormFile? file, [FromForm] string? version, [FromForm] string? reason, HbpService service, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return Error.Field("file", "Choose NHA's HBP master to import. " + HbpMasterFile.AcceptedFiles).ToProblem();
        }

        if (file.Length > HbpMasterFile.MaxBytes)
        {
            return Error.Field("file", "The file is larger than 10 MB, more than an HBP master is.").ToProblem();
        }

        if (string.IsNullOrWhiteSpace(version) || version.Trim().Length > 64 || version.Any(char.IsControl))
        {
            return Error.Field("version", "Name the master as NHA does (for example HBP 2022), in at most 64 characters.").ToProblem();
        }

        if (reason is not null && (reason.Trim().Length > 500 || reason.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')))
        {
            return Error.Field("reason", "Say why in at most 500 characters, without characters that are not allowed.").ToProblem();
        }

        using var buffer = new MemoryStream((int)file.Length);
        await using (var stream = file.OpenReadStream())
        {
            await stream.CopyToAsync(buffer, ct);
        }

        var result = await service.StageAsync(buffer.ToArray(), file.FileName, version, reason, ct);
        return result.IsFailure
            ? result.Error!.ToProblem()
            : TypedResults.Accepted($"/api/v1/approvals/{result.Value.Approval.Id}", result.Value);
    }
}
