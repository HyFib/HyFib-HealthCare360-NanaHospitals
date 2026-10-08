using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;

/// <summary>/api/v1/purchase/grns — goods receipt notes against vendor invoices (INV-03).</summary>
internal static class GoodsReceiptEndpoints
{
    private static readonly string[] Readers = [SupplyChainPermissions.GrnReceive, SupplyChainPermissions.PurchaseManage, SupplyChainPermissions.PurchaseApprove];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/purchase/grns").WithTags("Purchase");

        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapPost("/", async (CreateGoodsReceiptRequest r, GoodsReceiptService grns, CancellationToken ct) =>
                (await grns.CreateAsync(r, ct)).ToCreated(g => $"/api/v1/purchase/grns/{g.Id}"))
            .RequirePermission(SupplyChainPermissions.GrnReceive).WithValidation<CreateGoodsReceiptRequest>();
        group.MapPost("/{id:guid}/post", async (Guid id, GoodsReceiptService grns, CancellationToken ct) => (await grns.PostAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.GrnReceive);
        group.MapPost("/{id:guid}/cancel", async (Guid id, InventoryReasonRequest r, GoodsReceiptService grns, CancellationToken ct) =>
                (await grns.CancelAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.GrnReceive).WithValidation<InventoryReasonRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, GoodsReceiptStatus? status, Guid? vendorId, Guid? storeId, DateOnly? from, DateOnly? to, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var receipts = db.Set<GoodsReceiptNote>().AsNoTracking();
        if (status is not null)
        {
            receipts = receipts.Where(g => g.Status == status);
        }

        if (vendorId is not null)
        {
            receipts = receipts.Where(g => g.VendorId == vendorId);
        }

        if (storeId is not null)
        {
            receipts = receipts.Where(g => g.StoreId == storeId);
        }

        if (from is not null)
        {
            receipts = receipts.Where(g => g.ReceivedOn >= from);
        }

        if (to is not null)
        {
            receipts = receipts.Where(g => g.ReceivedOn <= to);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            receipts = receipts.Where(g => g.Number == upper || g.VendorInvoiceNumber == upper);
        }

        var result = await receipts.OrderByDescending(g => g.ReceivedOn).ThenByDescending(g => g.CreatedAt)
            .Select(g => new GoodsReceiptListItem(
                g.Id, g.Number, g.VendorId,
                db.Set<Vendor>().Where(v => v.Id == g.VendorId).Select(v => v.Name).FirstOrDefault() ?? string.Empty,
                g.StoreId,
                db.Set<Store>().Where(s => s.Id == g.StoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty,
                g.VendorInvoiceNumber, g.VendorInvoiceDate, g.ReceivedOn, g.Status, g.NetAmount, g.PurchaseOrderId))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var g = await db.Set<GoodsReceiptNote>().AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null)
        {
            return Error.NotFound(nameof(GoodsReceiptNote), id).ToProblem();
        }

        var vendor = await db.Set<Vendor>().AsNoTracking().Where(v => v.Id == g.VendorId).Select(v => new { v.Name, v.Gstin }).FirstOrDefaultAsync(ct);
        var storeName = await db.Set<Store>().Where(s => s.Id == g.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var itemIds = g.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Set<Item>().AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Code, i.BaseUnit })
            .ToDictionaryAsync(i => i.Id, ct);
        return TypedResults.Ok(new GoodsReceiptDetail(
            g.Id, g.Number, g.VendorId, vendor?.Name ?? string.Empty, vendor?.Gstin, g.StoreId, storeName, g.PurchaseOrderId, g.VendorInvoiceNumber,
            g.VendorInvoiceDate, g.ReceivedOn, g.IsInterState, g.Status, g.Remarks, g.PostedAt, g.GrossAmount, g.DiscountAmount, g.TaxableAmount,
            g.CgstAmount, g.SgstAmount, g.IgstAmount, g.RoundOff, g.NetAmount,
            g.Lines.OrderBy(l => l.ItemName).Select(l => new GoodsReceiptLineDto(
                l.Id, l.ItemId, l.ItemName, l.PurchaseOrderLineId, l.BatchNumber, l.ManufactureDate, l.ExpiryDate, l.Quantity, l.FreeQuantity, l.Rate,
                l.Mrp, l.DiscountPercent, l.GstRate, l.TaxableAmount, l.CgstAmount, l.SgstAmount, l.IgstAmount, l.LineTotal, l.BaseQuantity, l.UnitCost,
                l.UnitMrp, l.StockBatchId, items.GetValueOrDefault(l.ItemId)?.Code, items.GetValueOrDefault(l.ItemId)?.BaseUnit)).ToList(),
            g.Version));
    }
}
