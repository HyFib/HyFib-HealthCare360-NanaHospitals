using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;

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
        group.MapPost("/items", (SaveItemRequest r, HospitalDbContext db, DrugClassApprovals classes, ItemPriceApprovals prices, CancellationToken ct) => SaveItem(null, r, db, classes, prices, ct))
            .RequirePermission(SupplyChainPermissions.ItemsManage).WithValidation<SaveItemRequest>();
        group.MapPut("/items/{id:guid}", (Guid id, SaveItemRequest r, HospitalDbContext db, DrugClassApprovals classes, ItemPriceApprovals prices, CancellationToken ct) => SaveItem(id, r, db, classes, prices, ct))
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
        TelemedicineList? telemedicineList,
        bool? telemedicineUnlisted,
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

        // The committee works through the drugs on no list yet, and checks what it put on each.
        if (telemedicineList is not null)
        {
            items = items.Where(i => i.TelemedicineList == telemedicineList);
        }

        if (telemedicineUnlisted == true)
        {
            items = items.Where(i => i.Type == ItemType.Drug && i.TelemedicineList == null);
        }

        if (controlledOnly == true)
        {
            items = items.Where(i => i.IsControlled || i.Schedule == DrugSchedule.H1 || i.Schedule == DrugSchedule.X);
        }

        var result = await items.OrderByKey(page, ItemSort, i => i.Name)
            .Select(i => new ItemListItem(
                i.Id, i.Code, i.Name, i.GenericName, i.Strength, i.DosageForm, i.Type,
                db.Set<ItemCategory>().Where(c => c.Id == i.CategoryId).Select(c => c.Name).FirstOrDefault(),
                i.Schedule, i.IsControlled, i.IsColdChain, i.BaseUnit, i.PurchaseUnit, i.GstRate, i.ReorderLevel, i.IsActive, i.IsHighAlert, i.TelemedicineList))
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
                i.PurchaseUnit, i.PurchaseUnitFactor, i.GstRate, stock?.Available, stock?.CurrentMrp, stock?.NearestExpiry, i.IsHighAlert);
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

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, DrugClassApprovals classes, ItemPriceApprovals prices, CancellationToken ct)
    {
        var item = await db.Set<Item>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        return item is null
            ? Error.NotFound(nameof(Item), id).ToProblem()
            : TypedResults.Ok(ToDetail(item) with
            {
                PendingClass = await classes.PendingAsync(item.Id, ct),
                PendingPricing = await prices.PendingAsync(item.Id, ct),
            });
    }

    /// <summary>
    /// Saves an item. On an item already in the master, a drug made less strictly handled (<see cref="DrugSafetyClass"/>)
    /// keeps its class, and a ceiling price raised or removed or a GST rate changed (<see cref="ItemPricing"/>) keeps the
    /// item's pricing, until a second person approves the change (202 with the request; with both, the classification's,
    /// the pricing's waiting beside it in Approvals); the rest is saved at once.
    /// </summary>
    private static async Task<IResult> SaveItem(
        Guid? id, SaveItemRequest request, HospitalDbContext db, DrugClassApprovals classes, ItemPriceApprovals prices, CancellationToken ct)
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
        DrugSafetyClass? before = null;
        ItemPricing? pricedBefore = null;
        if (id is { } existingId)
        {
            var found = await db.Set<Item>().FirstOrDefaultAsync(i => i.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Item), existingId).ToProblem();
            }

            item = found;
            before = DrugSafetyClass.Of(item);
            pricedBefore = ItemPricing.Of(item);
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
        item.IsHighAlert = request.IsHighAlert;

        // A Schedule X or NDPS medicine is on the Prohibited List by law: saved on no list, it is put there.
        item.TelemedicineList = request.TelemedicineList is null && TelemedicineLists.ByLaw(request.Schedule, request.IsControlled)
            ? TelemedicineLists.Prohibited
            : request.TelemedicineList;
        item.IsColdChain = request.IsColdChain;
        item.VedClass = request.VedClass;
        item.ReorderLevel = request.ReorderLevel;
        item.ReorderQuantity = request.ReorderQuantity;
        item.MinimumStock = request.MinimumStock;
        item.MaximumStock = request.MaximumStock;
        item.Manufacturer = Clean(request.Manufacturer);
        item.Specification = Clean(request.Specification);
        item.CeilingPrice = request.CeilingPrice;
        item.CeilingNotification = request.CeilingPrice is null ? null : Clean(request.CeilingNotification);
        item.Barcode = barcode;
        item.IsActive = request.IsActive;

        var valid = item.Validate();
        if (valid.IsFailure)
        {
            return valid.Error!.ToProblem();
        }

        // Handled less strictly, the drug keeps its class until a second person approves; while a change waits, a save
        // asks for the same class or keeps the current one. Made stricter, it applies at once.
        var asked = DrugSafetyClass.Of(item);
        var ask = false;
        if (before is not null && before.IsWeakenedBy(asked))
        {
            var waiting = await classes.PendingAsync(item.Id, ct);
            if (waiting is not null && waiting.Asked != asked)
            {
                return Error.Conflict(DrugClassApprovals.WaitingCode,
                        $"A change to {item.Code} {item.Name}'s classification, asked for by {waiting.RequestedBy}, is waiting for approval. Withdraw it in Approvals first, or wait for its decision.")
                    .ToProblem();
            }

            before.ApplyTo(item);
            valid = item.Validate();
            if (valid.IsFailure)
            {
                return valid.Error!.ToProblem();
            }

            ask = waiting is null;
        }

        // A ceiling raised or removed, or another GST rate, keeps the item's pricing until a second person approves; a
        // ceiling set or lowered applies at once (DPCO: a price cut is in force from its notification).
        var pricing = ItemPricing.Of(item);
        var askPrice = false;
        if (pricedBefore is not null && pricedBefore.IsLoosenedBy(pricing))
        {
            var waiting = await prices.PendingAsync(item.Id, ct);
            if (waiting is not null && waiting.Asked != pricing)
            {
                return Error.Conflict(ItemPriceApprovals.WaitingCode,
                        $"A change to {item.Code} {item.Name}'s ceiling price or GST rate, asked for by {waiting.RequestedBy}, is waiting for approval. Withdraw it in Approvals first, or wait for its decision.")
                    .ToProblem();
            }

            pricedBefore.ApplyTo(item);
            valid = item.Validate();
            if (valid.IsFailure)
            {
                return valid.Error!.ToProblem();
            }

            askPrice = waiting is null;
        }

        // The item, and the requests to loosen its controls, are kept together or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        ApprovalRequestDto? accepted = null;
        if (askPrice)
        {
            var approval = await prices.AskAsync(item, pricedBefore!, pricing, request.PriceChangeReason, ct);
            if (approval.IsFailure)
            {
                return approval.Error!.ToProblem();
            }

            accepted = approval.Value;
        }

        if (ask)
        {
            var approval = await classes.AskAsync(item, before!, asked, request.ClassChangeReason, ct);
            if (approval.IsFailure)
            {
                return approval.Error!.ToProblem();
            }

            accepted = approval.Value;
        }

        if (accepted is not null)
        {
            await transaction.CommitAsync(ct);
            return TypedResults.Accepted($"/api/v1/approvals/{accepted.Id}", accepted);
        }

        await transaction.CommitAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/inventory/items/{item.Id}", new { item.Id }) : TypedResults.NoContent();
    }

    internal static ItemDetail ToDetail(Item i) => new(
        i.Id, i.Code, i.Name, i.GenericName, i.BrandName, i.Type, i.CategoryId, i.Strength, i.DosageForm, i.PackSize, i.BaseUnit, i.PurchaseUnit,
        i.PurchaseUnitFactor, i.HsnCode, i.GstRate, i.Schedule, i.IsControlled, i.IsColdChain, i.VedClass, i.ReorderLevel, i.ReorderQuantity,
        i.MinimumStock, i.MaximumStock, i.Manufacturer, i.Barcode, i.IsActive, i.RequiresPrescription, i.RequiresRegister, i.Version,
        i.Specification, i.CeilingPrice, i.IsHighAlert, CeilingNotification: i.CeilingNotification, TelemedicineList: i.TelemedicineList);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
