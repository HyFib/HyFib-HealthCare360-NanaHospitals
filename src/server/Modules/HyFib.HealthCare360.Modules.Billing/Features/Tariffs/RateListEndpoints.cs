using HyFib.HealthCare360.Modules.Billing.Documents;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Billing.Features.Tariffs;

/// <summary>
/// /api/v1/billing/rate-list — the rates a self-paying patient is charged at the signed-in facility today, and the list
/// to print for display in English and the local language (Clinical Establishments Rules 2012, r.9(i)).
/// </summary>
internal static class RateListEndpoints
{
    private static readonly string[] Readers =
        [BillingPermissions.TariffsManage, BillingPermissions.ChargesRead, BillingPermissions.OpCreate, BillingPermissions.IpManage, BillingPermissions.PaymentsCollect];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/rate-list").WithTags("Billing - Rate list");

        group.MapGet("/", Get).RequireAnyPermission(Readers);
        group.MapGet("/document", Document).RequireAnyPermission(Readers);
        group.MapGet("/pdf", Pdf).RequireAnyPermission(Readers);
    }

    private static async Task<IResult> Get(RateListService rates, ICurrentUser currentUser, CancellationToken ct)
    {
        var (view, problem) = await OpenAsync(rates, currentUser, ct);
        return problem ?? TypedResults.Ok(view);
    }

    private static async Task<IResult> Document(RateListService rates, DocumentAssets assets, ICurrentUser currentUser, CancellationToken ct)
    {
        var (view, problem) = await OpenAsync(rates, currentUser, ct);
        return problem ?? TypedResults.Ok((await RateListDocument.BuildAsync(currentUser.FacilityId!.Value, view!.Rates, view.Language, assets, ct)).ToDto());
    }

    private static async Task<IResult> Pdf(
        RateListService rates, DocumentAssets assets, IPdfRenderer renderer, ICurrentUser currentUser, CancellationToken ct)
    {
        var (view, problem) = await OpenAsync(rates, currentUser, ct);
        return problem ?? await (await RateListDocument.BuildAsync(currentUser.FacilityId!.Value, view!.Rates, view.Language, assets, ct)).ToPdfAsync(renderer, ct);
    }

    private static async Task<(RateListView? View, IResult? Problem)> OpenAsync(RateListService rates, ICurrentUser currentUser, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return (null, BillingDb.FacilityRequired.ToProblem());
        }

        return await rates.ForFacilityAsync(facilityId, ct) is { } view
            ? (view, null)
            : (null, Error.NotFound("Facility", facilityId).ToProblem());
    }
}
