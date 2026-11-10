using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;

/// <summary>A store's own reorder levels for an item, beside the item master's levels they override.</summary>
public sealed record StoreItemLevelDto(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    string BaseUnit,
    decimal ReorderLevel,
    decimal ReorderQuantity,
    decimal MinimumStock,
    decimal MaximumStock,
    decimal ItemReorderLevel,
    decimal ItemReorderQuantity,
    decimal ItemMinimumStock,
    decimal ItemMaximumStock,
    uint Version);

/// <param name="Version">The version loaded; leave empty when the store has no levels of its own for the item yet.</param>
public sealed record SaveStoreItemLevelRequest(decimal ReorderLevel, decimal ReorderQuantity, decimal MinimumStock, decimal MaximumStock, uint? Version);

public sealed class SaveStoreItemLevelRequestValidator : AbstractValidator<SaveStoreItemLevelRequest>
{
    public SaveStoreItemLevelRequestValidator()
    {
        RuleFor(x => x.ReorderLevel).InclusiveBetween(0, 10_000_000);
        RuleFor(x => x.ReorderQuantity).InclusiveBetween(0, 10_000_000);
        RuleFor(x => x.MinimumStock).InclusiveBetween(0, 10_000_000);
        RuleFor(x => x.MaximumStock).InclusiveBetween(0, 10_000_000)
            .Must((r, max) => max == 0 || max >= r.ReorderLevel).WithMessage("The maximum stock cannot be under the reorder level.");
    }
}

/// <summary>
/// /api/v1/inventory/stores/{storeId}/levels — a store's own reorder levels, overriding the item master's (INV-08). Every
/// change is audited with the levels before and after; none waits for a second person (see <see cref="StoreItemLevel"/>).
/// </summary>
internal static class StoreLevelEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inventory/stores/{storeId:guid}/levels").WithTags("Inventory");

        group.MapGet("/", List).RequireAnyPermission(SupplyChainPermissions.ItemsManage, SupplyChainPermissions.StockRead);
        group.MapPut("/{itemId:guid}", Save)
            .RequirePermission(SupplyChainPermissions.ItemsManage).WithValidation<SaveStoreItemLevelRequest>().FirstSaveCreates();
        group.MapDelete("/{itemId:guid}", Remove).RequirePermission(SupplyChainPermissions.ItemsManage);
    }

    private static async Task<IResult> List(Guid storeId, HospitalDbContext db, CancellationToken ct)
    {
        var rows = await (
                from level in db.Set<StoreItemLevel>().AsNoTracking()
                join item in db.Set<Item>().AsNoTracking() on level.ItemId equals item.Id
                where level.StoreId == storeId
                orderby item.Name
                select new StoreItemLevelDto(
                    item.Id, item.Code, item.Name, item.BaseUnit, level.ReorderLevel, level.ReorderQuantity, level.MinimumStock, level.MaximumStock,
                    item.ReorderLevel, item.ReorderQuantity, item.MinimumStock, item.MaximumStock, level.Version))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> Save(
        Guid storeId, Guid itemId, SaveStoreItemLevelRequest request, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var store = await db.Set<Store>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        var item = await db.Set<Item>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (store is null || item is null)
        {
            return Error.NotFound(store is null ? nameof(Store) : nameof(Item), store is null ? storeId : itemId).ToProblem();
        }

        var level = await db.Set<StoreItemLevel>().FirstOrDefaultAsync(l => l.StoreId == storeId && l.ItemId == itemId, ct);
        var before = level?.Levels;
        if (level is null)
        {
            level = new StoreItemLevel { FacilityId = store.FacilityId, StoreId = storeId, ItemId = itemId };
            db.Add(level);
        }

        db.SetExpectedVersion(level, request.Version);
        var asked = new ReorderLevels(request.ReorderLevel, request.ReorderQuantity, request.MinimumStock, request.MaximumStock);
        var set = level.Set(asked);
        if (set.IsFailure)
        {
            return set.Error!.ToProblem();
        }

        var was = before is null ? $"the item's own: {ReorderCalculator.LevelsFor(item, null).Words(item.BaseUnit)}" : before.Words(item.BaseUnit);
        Audit(audit, level, "stock_level.saved",
            $"{item.Code} {item.Name} in {store.Name}: {asked.Words(item.BaseUnit)} (was {was})", new { before, after = asked });
        await db.SaveChangesAsync(ct);
        return before is null ? TypedResults.Created($"/api/v1/inventory/stores/{storeId}/levels", new { level.Id }) : TypedResults.NoContent();
    }

    /// <summary>The store goes back to the item master's levels.</summary>
    private static async Task<IResult> Remove(Guid storeId, Guid itemId, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var level = await db.Set<StoreItemLevel>().FirstOrDefaultAsync(l => l.StoreId == storeId && l.ItemId == itemId, ct);
        if (level is null)
        {
            return TypedResults.NoContent();
        }

        var item = await db.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId, ct);
        var store = await db.Set<Store>().AsNoTracking().Where(s => s.Id == storeId).Select(s => s.Name).FirstAsync(ct);
        var before = level.Levels;
        db.Remove(level);
        Audit(audit, level, "stock_level.removed",
            $"{item.Code} {item.Name} in {store}: the item's own levels, {ReorderCalculator.LevelsFor(item, null).Words(item.BaseUnit)} (was the store's: {before.Words(item.BaseUnit)})",
            new { before });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static void Audit(IAuditLogger audit, StoreItemLevel level, string action, string text, object details) =>
        audit.Add(new AuditEntry(AuditCategories.Data, action, text.Length <= 1000 ? text : text[..999] + "…")
        {
            EntityType = nameof(StoreItemLevel),
            EntityId = level.Id.ToString(),
            Details = details,
        });
}
