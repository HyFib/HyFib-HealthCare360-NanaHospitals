using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Items;

/// <summary>/api/v1/inventory — item categories and the item master (INV-01).</summary>
internal static class ItemEndpoints
{
    private static readonly Dictionary<string, Expression<Func<Item, object?>>> ItemSort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = i => i.Code,
        ["name"] = i => i.Name,
        ["genericName"] = i => i.GenericName,
        ["type"] = i => i.Type,
        ["gstRate"] = i => i.GstRate,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inventory").WithTags("Inventory");

        group.MapGet("/categories", ListCategories).RequireAnyPermission(SupplyChainPermissions.CatalogReaders);
        group.MapPost("/categories", (SaveItemCategoryRequest r, HospitalDbContext db, CancellationToken ct) => SaveCategory(null, r, db, ct))
            .RequirePermission(SupplyChainPermissions.ItemsManage).WithValidation<SaveItemCategoryRequest>();
        group.MapPut("/categories/{id:guid}", (Guid id, SaveItemCategoryRequest r, HospitalDbContext db, CancellationToken ct) => SaveCategory(id, r, db, ct))
            .RequirePermission(SupplyChainPermissions.ItemsManage).WithValidation<SaveItemCategoryRequest>().RequireVersion();

        group.MapGet("/items", Search).RequireAnyPermission(SupplyChainPermissions.CatalogReaders);
        group.MapGet("/items/lookup", Lookup).RequireAnyPermission(SupplyChainPermissions.CatalogReaders);
        group.MapGet("/items/by-code/{code}", ByCode).RequireAnyPermission(SupplyChainPermissions.CatalogReaders);
        group.MapGet("/items/{id:guid}", Get).RequireAnyPermission(SupplyChainPermissions.CatalogReaders);
        group.MapPost("/items", (SaveItemRequest r, HospitalDbContext db, CancellationToken ct) => SaveItem(null, r, db, ct))
            .RequirePermission(SupplyChainPermissions.ItemsManage).WithValidation<SaveItemRequest>();
        group.MapPut("/items/{id:guid}", (Guid id, SaveItemRequest r, HospitalDbContext db, CancellationToken ct) => SaveItem(id, r, db, ct))
            .RequirePermission(SupplyChainPermissions.ItemsManage).WithValidation<SaveItemRequest>().RequireVersion();
    }

    // ------------------------------------------------------------------ categories

    private static async Task<IResult> ListCategories(bool? activeOnly, HospitalDbContext db, CancellationToken ct)
    {
        var categories = db.Set<ItemCategory>().AsNoTracking();
        if (activeOnly == true)
        {
            categories = categories.Where(c => c.IsActive);
        }

        return TypedResults.Ok(await categories.OrderBy(c => c.Name)
            .Select(c => new ItemCategoryDto(c.Id, c.Code, c.Name, c.DefaultType, c.Description, c.IsActive, c.Version))
            .ToListAsync(ct));
    }

    private static async Task<IResult> SaveCategory(Guid? id, SaveItemCategoryRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<ItemCategory>().AnyAsync(c => c.Code == code && c.Id != id, ct))
        {
            return Error.Conflict("category.duplicate_code", $"Category code {code} is already used.").ToProblem();
        }

        ItemCategory category;
        if (id is { } existingId)
        {
            var found = await db.Set<ItemCategory>().FirstOrDefaultAsync(c => c.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(ItemCategory), existingId).ToProblem();
            }

            category = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(category, version);
            }
        }
        else
        {
            category = new ItemCategory();
            db.Add(category);
        }

        category.Code = code;
        category.Name = request.Name.Trim();
        category.DefaultType = request.DefaultType;
        category.Description = request.Description?.Trim();
        category.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/inventory/categories/{category.Id}", new { category.Id }) : TypedResults.NoContent();
    }

    // ------------------------------------------------------------------ items

    private static async Task<IResult> Search(
        [AsParameters] PageQuery query,
        ItemType? type,
        Guid? categoryId,
        DrugSchedule? schedule,
        bool? controlledOnly,
        bool? includeInactive,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var items = db.Set<Item>().AsNoTracking().Search(page.SearchTerm);
        if (includeInactive != true)
        {
            items = items.Where(i => i.IsActive);
        }

        if (type is not null)
        {
            items = items.Where(i => i.Type == type);
        }

        if (categoryId is not null)
        {
            items = items.Where(i => i.CategoryId == categoryId);
        }

        if (schedule is not null)
        {
            items = items.Where(i => i.Schedule == schedule);
        }

        if (controlledOnly == true)
        {
            items = items.Where(i => i.IsControlled || i.Schedule == DrugSchedule.H1 || i.Schedule == DrugSchedule.X);
        }

        var result = await items.OrderByKey(page, ItemSort, i => i.Name)
            .Select(i => new ItemListItem(
                i.Id, i.Code, i.Name, i.GenericName, i.Strength, i.DosageForm, i.Type,
                db.Set<ItemCategory>().Where(c => c.Id == i.CategoryId).Select(c => c.Name).FirstOrDefault(),
                i.Schedule, i.IsControlled, i.IsColdChain, i.BaseUnit, i.PurchaseUnit, i.GstRate, i.ReorderLevel, i.IsActive))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    /// <summary>Fast picker (20 rows) with stock in the store the user works in, for indents, sales and GRNs.</summary>
    private static async Task<IResult> Lookup(string? search, Guid? storeId, ItemType? type, int? take, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var items = db.Set<Item>().AsNoTracking().Where(i => i.IsActive).Search(search);
        if (type is not null)
        {
            items = items.Where(i => i.Type == type);
        }

        var rows = await items.OrderBy(i => i.Name).Take(Math.Clamp(take ?? 20, 1, 50)).ToListAsync(ct);
        var availability = storeId is { } store
            ? await InventoryQueries.AvailabilityAsync(db, store, rows.Select(i => i.Id).ToList(), HospitalClock.Today(time), ct)
            : new Dictionary<Guid, StockAvailability>();

        return TypedResults.Ok(rows.Select(i =>
        {
            var stock = availability.GetValueOrDefault(i.Id);
            return new ItemLookup(i.Id, i.Code, i.Name, i.GenericName, i.Strength, i.Type, i.Schedule, i.IsControlled, i.IsColdChain, i.BaseUnit,
                i.PurchaseUnit, i.PurchaseUnitFactor, i.GstRate, stock?.Available, stock?.CurrentMrp, stock?.NearestExpiry);
        }).ToList());
    }

    /// <summary>Barcode scanners type the code or barcode into the focused field (keyboard wedge).</summary>
    private static async Task<IResult> ByCode(string code, HospitalDbContext db, CancellationToken ct)
    {
        var normalized = code.Trim();
        var upper = normalized.ToUpperInvariant();
        var item = await db.Set<Item>().AsNoTracking().FirstOrDefaultAsync(i => i.Barcode == normalized || i.Code == upper, ct);
        return item is null ? Error.NotFound(nameof(Item), normalized).ToProblem() : TypedResults.Ok(ToDetail(item));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var item = await db.Set<Item>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        return item is null ? Error.NotFound(nameof(Item), id).ToProblem() : TypedResults.Ok(ToDetail(item));
    }

    private static async Task<IResult> SaveItem(Guid? id, SaveItemRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Item>().AnyAsync(i => i.Code == code && i.Id != id, ct))
        {
            return Error.Conflict("item.duplicate_code", $"Item code {code} is already used.").ToProblem();
        }

        var barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim();
        if (barcode is not null && await db.Set<Item>().AnyAsync(i => i.Barcode == barcode && i.Id != id, ct))
        {
            return Error.Conflict("item.duplicate_barcode", $"Barcode {barcode} is already assigned to another item.").ToProblem();
        }

        if (request.CategoryId is { } categoryId && !await db.Set<ItemCategory>().AnyAsync(c => c.Id == categoryId, ct))
        {
            return Error.Field("categoryId", "Choose a valid category.").ToProblem();
        }

        Item item;
        if (id is { } existingId)
        {
            var found = await db.Set<Item>().FirstOrDefaultAsync(i => i.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Item), existingId).ToProblem();
            }

            item = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(item, version);
            }
        }
        else
        {
            item = new Item();
            db.Add(item);
        }

        item.Code = code;
        item.Name = request.Name.Trim();
        item.GenericName = Clean(request.GenericName);
        item.BrandName = Clean(request.BrandName);
        item.Type = request.Type;
        item.CategoryId = request.CategoryId;
        item.Strength = Clean(request.Strength);
        item.DosageForm = Clean(request.DosageForm);
        item.PackSize = Clean(request.PackSize);
        item.BaseUnit = request.BaseUnit.Trim().ToUpperInvariant();
        item.PurchaseUnit = request.PurchaseUnit.Trim().ToUpperInvariant();
        item.PurchaseUnitFactor = request.PurchaseUnitFactor;
        item.HsnCode = Clean(request.HsnCode);
        item.GstRate = request.GstRate;
        item.Schedule = request.Schedule;
        item.IsControlled = request.IsControlled;
        item.IsColdChain = request.IsColdChain;
        item.VedClass = request.VedClass;
        item.ReorderLevel = request.ReorderLevel;
        item.ReorderQuantity = request.ReorderQuantity;
        item.MinimumStock = request.MinimumStock;
        item.MaximumStock = request.MaximumStock;
        item.Manufacturer = Clean(request.Manufacturer);
        item.Barcode = barcode;
        item.IsActive = request.IsActive;

        var valid = item.Validate();
        if (valid.IsFailure)
        {
            return valid.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/inventory/items/{item.Id}", new { item.Id }) : TypedResults.NoContent();
    }

    internal static ItemDetail ToDetail(Item i) => new(
        i.Id, i.Code, i.Name, i.GenericName, i.BrandName, i.Type, i.CategoryId, i.Strength, i.DosageForm, i.PackSize, i.BaseUnit, i.PurchaseUnit,
        i.PurchaseUnitFactor, i.HsnCode, i.GstRate, i.Schedule, i.IsControlled, i.IsColdChain, i.VedClass, i.ReorderLevel, i.ReorderQuantity,
        i.MinimumStock, i.MaximumStock, i.Manufacturer, i.Barcode, i.IsActive, i.RequiresPrescription, i.RequiresRegister, i.Version);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
