using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using Microsoft.AspNetCore.Mvc;

namespace HyFib.HealthCare360.Modules.Billing.Features.Hbp;

/// <summary>
/// /api/v1/billing/hbp — AB PM-JAY's Health Benefit Package master: imported from NHA's file by whoever keeps the
/// tariffs, and looked up by them and by the insurance desk (pre-authorisations and claims to the scheme).
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

        // Bearer-token API (no cookie authentication), so antiforgery does not apply. A file larger than a master is
        // refused before it is read.
        group.MapPost("/imports", Import).RequirePermission(BillingPermissions.TariffsManage)
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(HbpMasterFile.MaxBytes + (64 * 1024)));
    }

    private static async Task<IResult> Search(string? search, string? specialty, bool? includeRetired, HbpService service, CancellationToken ct) =>
        TypedResults.Ok(await service.SearchAsync(search, specialty, includeRetired == true, ct));

    private static async Task<IResult> Get(string code, HbpService service, CancellationToken ct) =>
        await service.GetAsync(code, ct) is { } procedure
            ? TypedResults.Ok(procedure)
            : Error.NotFound("billing.hbp_not_found", $"{code} is not in the imported HBP master.").ToProblem();

    /// <summary>
    /// Multipart <c>file</c> (NHA's HBP master, .xlsx or CSV, at most 10 MB) and <c>version</c> (its name, for example
    /// "HBP 2022"). 200 with what changed and the rows not read; 400 <c>file</c> or <c>version</c> otherwise.
    /// </summary>
    private static async Task<IResult> Import(IFormFile? file, [FromForm] string? version, HbpService service, CancellationToken ct)
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

        using var buffer = new MemoryStream((int)file.Length);
        await using (var stream = file.OpenReadStream())
        {
            await stream.CopyToAsync(buffer, ct);
        }

        var result = await service.ImportAsync(buffer.ToArray(), version, ct);
        return result.IsFailure ? result.Error!.ToProblem() : TypedResults.Ok(result.Value);
    }
}
