using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

/// <summary>
/// /api/v1/ot/store-items — the OT store look-up for the implants and consumables dialog (OT-05). OT staff cannot call
/// the inventory endpoints, so this lists only what recording a material needs, from the same OT store that
/// <see cref="OtClinicalService.AddMaterialAsync"/> issues from.
/// </summary>
internal static class OtStoreEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ot/store-items", StoreItems).WithTags("Operation Theatre").RequirePermission(SpecialtyCarePermissions.OtMaterials);

    /// <summary>
    /// Items with issuable stock in the facility's OT store (name order, at most 50). 422 <c>stock.no_store</c> when the
    /// facility has no active OT store, <c>stock.store_ambiguous</c> when it has more than one.
    /// </summary>
    private static async Task<IResult> StoreItems(
        string? search, int? take, HospitalDbContext db, StockService stock, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var store = await stock.ResolveStoreAsync(facilityId, StoreType.OT, ct);
        if (store.IsFailure)
        {
            return store.Error!.ToProblem();
        }

        var today = time.Today();
        var items = await OtStoreLookup.Candidates(db.Set<Item>().AsNoTracking(), db.Set<StockBatch>(), store.Value, today, search, take).ToListAsync(ct);
        var batches = items.Count == 0
            ? new List<OtStoreBatchRow>()
            : await OtStoreLookup.Batches(db.Set<StockBatch>().AsNoTracking(), store.Value, items.Select(i => i.Id).ToList()).ToListAsync(ct);
        return TypedResults.Ok(items.Select(i => OtStoreLookup.ToItem(i, batches, today)).ToList());
    }
}
