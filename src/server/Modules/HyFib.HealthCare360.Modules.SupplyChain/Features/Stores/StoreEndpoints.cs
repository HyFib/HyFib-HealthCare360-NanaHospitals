using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;

public sealed record StoreDto(
    Guid Id,
    string Code,
    string Name,
    StoreType Type,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? InChargeUserId,
    string? InChargeName,
    string? Location,
    bool IsActive,
    uint Version);

public sealed record SaveStoreRequest(string Code, string Name, StoreType Type, Guid? DepartmentId, Guid? InChargeUserId, string? Location, bool IsActive, uint? Version);

public sealed class SaveStoreRequestValidator : AbstractValidator<SaveStoreRequest>
{
    public SaveStoreRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Must(CatalogRules.ValidCode).WithMessage("Use letters, digits, '-', '_' or '/'.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Location).SafeText(200);
    }
}

/// <summary>/api/v1/inventory/stores — store master of the signed-in facility (central store, pharmacies, sub-stores).</summary>
internal static class StoreEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inventory/stores").WithTags("Inventory");

        group.MapGet("/", List).RequireAnyPermission([.. SupplyChainPermissions.CatalogReaders, SupplyChainPermissions.AdjustmentsApprove]);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(SupplyChainPermissions.CatalogReaders);
        group.MapPost("/", (SaveStoreRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => Save(null, r, db, u, ct))
            .RequirePermission(SupplyChainPermissions.ItemsManage).WithValidation<SaveStoreRequest>();
        group.MapPut("/{id:guid}", (Guid id, SaveStoreRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => Save(id, r, db, u, ct))
            .RequirePermission(SupplyChainPermissions.ItemsManage).WithValidation<SaveStoreRequest>();
    }

    private static async Task<IResult> List(StoreType? type, bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var stores = db.Set<Store>().AsNoTracking();
        if (includeInactive != true)
        {
            stores = stores.Where(s => s.IsActive);
        }

        if (type is not null)
        {
            stores = stores.Where(s => s.Type == type);
        }

        // Store types are stored as text, so SQL would order them alphabetically; the list is short, so order it here
        // (central store, pharmacies, wards, OT, lab, canteen, CSSD).
        var rows = await Project(stores, db).ToListAsync(ct);
        return TypedResults.Ok(rows.OrderBy(s => s.Type).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var store = await Project(db.Set<Store>().AsNoTracking().Where(s => s.Id == id), db).FirstOrDefaultAsync(ct);
        return store is null ? Error.NotFound(nameof(Store), id).ToProblem() : TypedResults.Ok(store);
    }

    private static async Task<IResult> Save(Guid? id, SaveStoreRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Store>().AnyAsync(s => s.Code == code && s.Id != id, ct))
        {
            return Error.Conflict("store.duplicate_code", $"Store code {code} is already used in this facility.").ToProblem();
        }

        if (request.DepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId, ct))
        {
            return Error.Field("departmentId", "Choose a department of this facility.").ToProblem();
        }

        Store store;
        if (id is { } existingId)
        {
            var found = await db.Set<Store>().FirstOrDefaultAsync(s => s.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Store), existingId).ToProblem();
            }

            store = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(store, version);
            }

            if (store.IsActive && !request.IsActive && await db.Set<StockBatch>().AnyAsync(b => b.StoreId == existingId && b.QuantityOnHand > 0, ct))
            {
                return Error.Rule("store.has_stock", "This store still holds stock. Transfer or write it off before deactivating the store.").ToProblem();
            }
        }
        else
        {
            store = new Store { FacilityId = facilityId };
            db.Add(store);
        }

        store.Code = code;
        store.Name = request.Name.Trim();
        store.Type = request.Type;
        store.DepartmentId = request.DepartmentId;
        store.InChargeUserId = request.InChargeUserId;
        store.Location = request.Location?.Trim();
        store.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/inventory/stores/{store.Id}", new { store.Id }) : TypedResults.NoContent();
    }

    private static IQueryable<StoreDto> Project(IQueryable<Store> stores, HospitalDbContext db) =>
        stores.Select(s => new StoreDto(
            s.Id, s.Code, s.Name, s.Type, s.DepartmentId,
            db.Set<Department>().Where(d => d.Id == s.DepartmentId).Select(d => d.Name).FirstOrDefault(),
            s.InChargeUserId,
            db.Set<User>().Where(u => u.Id == s.InChargeUserId).Select(u => u.DisplayName).FirstOrDefault(),
            s.Location, s.IsActive, s.Version));
}
