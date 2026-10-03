using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Gateway;

/// <summary>
/// /api/v1/billing/gateway — reconciliation of online payments (INT-PG, INT-POS "reconcile"): the gateway's settlements
/// by day, the exceptions worst first, and what an accountant can do about each one. Everything is limited to the
/// signed-in facility and needs <see cref="BillingPermissions.GatewayReconcile"/>. Screens show document numbers and
/// gateway ids only, never patients' names.
/// </summary>
internal static class ReconciliationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/gateway").WithTags("Billing - Gateway reconciliation")
            .RequirePermission(BillingPermissions.GatewayReconcile);
        group.MapGet("/summary", Summary);
        group.MapGet("/settlements", ListSettlements);
        group.MapGet("/settlements/{id:guid}", GetSettlement);
        group.MapPost("/settlements/fetch", Fetch).WithValidation<FetchSettlementsRequest>();
        group.MapGet("/exceptions", ListExceptions);
        group.MapGet("/exceptions/{id:guid}", GetException);
        group.MapPost("/exceptions/{id:guid}/record-receipt", RecordReceipt);
        group.MapPost("/exceptions/{id:guid}/retry", Retry);
        group.MapPost("/exceptions/{id:guid}/resolve", Resolve).WithValidation<ResolveDiscrepancyRequest>();
    }

    private static async Task<IResult> Summary(ICurrentUser user, GatewayDiscrepancyService discrepancies, CancellationToken ct) =>
        user.FacilityId is { } facilityId ? TypedResults.Ok(await discrepancies.SummaryAsync(facilityId, ct)) : BillingDb.FacilityRequired.ToProblem();

    /// <summary>Settlements by day between <paramref name="from"/> and <paramref name="to"/> (at most 92 days; the last week by default).</summary>
    private static async Task<IResult> ListSettlements(
        DateOnly? from, DateOnly? to, ICurrentUser user, GatewaySettlementService settlements, TimeProvider time, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var last = to ?? BillingTime.Today(time);
        var first = from ?? last.AddDays(-6);
        if (first > last)
        {
            return Error.Field("from", "The start must be on or before the end.").ToProblem();
        }

        if (last.DayNumber - first.DayNumber >= GatewaySettlementService.MaxListDays)
        {
            return Error.Field("from", $"Choose at most {GatewaySettlementService.MaxListDays} days.").ToProblem();
        }

        return TypedResults.Ok(await settlements.ListAsync(facilityId, first, last, ct));
    }

    private static async Task<IResult> GetSettlement(Guid id, ICurrentUser user, GatewaySettlementService settlements, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        return await settlements.GetAsync(facilityId, id, ct) is { } detail
            ? TypedResults.Ok(detail)
            : Error.NotFound(nameof(GatewaySettlement), id).ToProblem();
    }

    /// <summary>Reads one day's settlements from the gateway now, for the signed-in facility's merchant account.</summary>
    private static async Task<IResult> Fetch(FetchSettlementsRequest request, ICurrentUser user, GatewaySettlementService settlements, CancellationToken ct) =>
        user.FacilityId is { } facilityId ? (await settlements.FetchAsync(facilityId, request.Date, ct)).ToOk() : BillingDb.FacilityRequired.ToProblem();

    // The exceptions are limited to the signed-in facility by the facility query filter, which does not apply to a
    // user without one: such a user is refused, as on the endpoints above.

    /// <summary>Exceptions worst first (open by default), or the resolved ones newest first.</summary>
    private static async Task<IResult> ListExceptions(
        [AsParameters] PageQuery query, DiscrepancyStatus? status, DiscrepancyKind? kind, ICurrentUser user,
        GatewayDiscrepancyService discrepancies, CancellationToken ct) =>
        user.FacilityId is null
            ? BillingDb.FacilityRequired.ToProblem()
            : TypedResults.Ok(await discrepancies.ListAsync(query.ToRequest(), status, kind, ct));

    private static async Task<IResult> GetException(
        Guid id, ICurrentUser user, GatewayDiscrepancyService discrepancies, CancellationToken ct) =>
        user.FacilityId is null ? BillingDb.FacilityRequired.ToProblem()
        : await discrepancies.GetAsync(id, ct) is { } dto ? TypedResults.Ok(dto)
        : Error.NotFound(nameof(GatewayDiscrepancy), id).ToProblem();

    private static async Task<IResult> RecordReceipt(
        Guid id, ICurrentUser user, GatewayDiscrepancyActions actions, CancellationToken ct) =>
        user.FacilityId is null
            ? BillingDb.FacilityRequired.ToProblem()
            : (await actions.RecordReceiptAsync(id, ct)).ToOk();

    private static async Task<IResult> Retry(
        Guid id, ICurrentUser user, GatewayDiscrepancyActions actions, CancellationToken ct) =>
        user.FacilityId is null ? BillingDb.FacilityRequired.ToProblem() : (await actions.RetryAsync(id, ct)).ToOk();

    private static async Task<IResult> Resolve(
        Guid id, ResolveDiscrepancyRequest request, ICurrentUser user, GatewayDiscrepancyService discrepancies,
        CancellationToken ct) =>
        user.FacilityId is null
            ? BillingDb.FacilityRequired.ToProblem()
            : (await discrepancies.ResolveAsync(id, request, ct)).ToOk();
}
