using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;

/// <summary>/api/v1/purchase — requisitions and purchase orders with approval (INV-03).</summary>
internal static class PurchaseEndpoints
{
    private static readonly string[] Readers = [SupplyChainPermissions.PurchaseManage, SupplyChainPermissions.PurchaseApprove, SupplyChainPermissions.GrnReceive];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/purchase").WithTags("Purchase");

        group.MapGet("/requisitions", ListRequisitions).RequireAnyPermission(Readers);
        group.MapGet("/requisitions/{id:guid}", GetRequisition).RequireAnyPermission(Readers);
        group.MapPost("/requisitions", async (CreateRequisitionRequest r, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.CreateRequisitionAsync(r, ct)).ToCreated(d => $"/api/v1/purchase/requisitions/{d.Id}"))
            .RequirePermission(SupplyChainPermissions.PurchaseManage).WithValidation<CreateRequisitionRequest>();
        group.MapPost("/requisitions/from-reorder", async (Guid storeId, PurchaseService purchase, InventoryReportService reports, CancellationToken ct) =>
                (await purchase.CreateRequisitionFromReorderAsync(storeId, reports, ct)).ToCreated(d => $"/api/v1/purchase/requisitions/{d.Id}"))
            .RequirePermission(SupplyChainPermissions.PurchaseManage);
        group.MapPost("/requisitions/{id:guid}/approve", async (Guid id, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.ApproveRequisitionAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseApprove);
        group.MapPost("/requisitions/{id:guid}/reject", async (Guid id, InventoryReasonRequest r, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.RejectRequisitionAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseApprove).WithValidation<InventoryReasonRequest>();
        group.MapPost("/requisitions/{id:guid}/cancel", async (Guid id, InventoryReasonRequest r, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.CancelRequisitionAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseManage).WithValidation<InventoryReasonRequest>();

        group.MapGet("/orders", ListOrders).RequireAnyPermission(Readers);
        group.MapGet("/orders/{id:guid}", GetOrder).RequireAnyPermission(Readers);
        group.MapPost("/orders", async (SavePurchaseOrderRequest r, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.CreateOrderAsync(r, ct)).ToCreated(d => $"/api/v1/purchase/orders/{d.Id}"))
            .RequirePermission(SupplyChainPermissions.PurchaseManage).WithValidation<SavePurchaseOrderRequest>();
        group.MapPut("/orders/{id:guid}", async (Guid id, SavePurchaseOrderRequest r, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.UpdateOrderAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseManage).WithValidation<SavePurchaseOrderRequest>().RequireVersion();
        group.MapPost("/orders/{id:guid}/submit", async (Guid id, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.SubmitOrderAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseManage);
        group.MapPost("/orders/{id:guid}/approve", async (Guid id, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.ApproveOrderAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseApprove);
        group.MapPost("/orders/{id:guid}/return", async (Guid id, InventoryReasonRequest r, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.ReturnOrderAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseApprove).WithValidation<InventoryReasonRequest>();
        group.MapPost("/orders/{id:guid}/send", async (Guid id, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.SendOrderAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseManage);
        group.MapPost("/orders/{id:guid}/close", async (Guid id, InventoryReasonRequest r, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.CloseOrderAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseManage).WithValidation<InventoryReasonRequest>();
        group.MapPost("/orders/{id:guid}/cancel", async (Guid id, InventoryReasonRequest r, PurchaseService purchase, CancellationToken ct) =>
                (await purchase.CancelOrderAsync(id, r.Reason, ct)).ToNoContent())
            .RequireAnyPermission(SupplyChainPermissions.PurchaseManage, SupplyChainPermissions.PurchaseApprove).WithValidation<InventoryReasonRequest>();
    }

    private static async Task<IResult> ListRequisitions([AsParameters] PageQuery query, RequisitionStatus? status, Guid? storeId, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var requisitions = db.Set<PurchaseRequisition>().AsNoTracking();
        if (status is not null)
        {
            requisitions = requisitions.Where(r => r.Status == status);
        }

        if (storeId is not null)
        {
            requisitions = requisitions.Where(r => r.StoreId == storeId);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            requisitions = requisitions.Where(r => r.Number == upper);
        }

        var result = await requisitions.OrderByDescending(r => r.CreatedAt)
            .Select(r => new RequisitionListItem(
                r.Id, r.Number, r.StoreId,
                db.Set<Store>().Where(s => s.Id == r.StoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty,
                r.Status, r.RequiredBy, r.Lines.Count, r.CreatedAt,
                db.Set<User>().Where(u => u.Id == r.RequestedByUserId).Select(u => u.DisplayName).FirstOrDefault()))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetRequisition(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var r = await db.Set<PurchaseRequisition>().AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
        {
            return Error.NotFound(nameof(PurchaseRequisition), id).ToProblem();
        }

        var storeName = await db.Set<Store>().Where(s => s.Id == r.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        return TypedResults.Ok(new RequisitionDetail(
            r.Id, r.Number, r.StoreId, storeName, r.Status, r.RequiredBy, r.Remarks, r.RejectReason, r.PurchaseOrderId, r.CreatedAt, r.DecidedAt,
            r.Lines.Select(l => new RequisitionLineDto(l.Id, l.ItemId, l.ItemName, l.Quantity, l.Remarks)).ToList(), r.Version));
    }

    private static async Task<IResult> ListOrders(
        [AsParameters] PageQuery query, PurchaseOrderStatus? status, Guid? vendorId, Guid? storeId, DateOnly? from, DateOnly? to, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var orders = db.Set<PurchaseOrder>().AsNoTracking();
        if (status is not null)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (vendorId is not null)
        {
            orders = orders.Where(o => o.VendorId == vendorId);
        }

        if (storeId is not null)
        {
            orders = orders.Where(o => o.StoreId == storeId);
        }

        if (from is not null)
        {
            orders = orders.Where(o => o.OrderDate >= from);
        }

        if (to is not null)
        {
            orders = orders.Where(o => o.OrderDate <= to);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            var like = term.ToLikePattern();
            orders = orders.Where(o => o.Number == upper || db.Set<Vendor>().Any(v => v.Id == o.VendorId && EF.Functions.ILike(v.Name, like, QueryExtensions.LikeEscape)));
        }

        var result = await orders.OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.CreatedAt)
            .Select(o => new PurchaseOrderListItem(
                o.Id, o.Number, o.VendorId,
                db.Set<Vendor>().Where(v => v.Id == o.VendorId).Select(v => v.Name).FirstOrDefault() ?? string.Empty,
                o.StoreId,
                db.Set<Store>().Where(s => s.Id == o.StoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty,
                o.OrderDate, o.ExpectedDeliveryDate, o.Status, o.NetAmount, o.Lines.Count))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetOrder(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var o = await db.Set<PurchaseOrder>().AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null)
        {
            return Error.NotFound(nameof(PurchaseOrder), id).ToProblem();
        }

        var vendor = await db.Set<Vendor>().AsNoTracking().Where(v => v.Id == o.VendorId).Select(v => new { v.Name, v.Gstin }).FirstOrDefaultAsync(ct);
        var storeName = await db.Set<Store>().Where(s => s.Id == o.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        return TypedResults.Ok(new PurchaseOrderDetail(
            o.Id, o.Number, o.VendorId, vendor?.Name ?? string.Empty, vendor?.Gstin, o.StoreId, storeName, o.RequisitionId, o.OrderDate,
            o.ExpectedDeliveryDate, o.IsInterState, o.PaymentTermsDays, o.Remarks, o.Status, o.ReviewNote, o.ApprovedAt, o.SentAt, o.ClosedAt,
            o.CloseReason, o.GrossAmount, o.DiscountAmount, o.TaxableAmount, o.CgstAmount, o.SgstAmount, o.IgstAmount, o.RoundOff, o.NetAmount,
            o.Lines.OrderBy(l => l.ItemName).Select(l => new PurchaseOrderLineDto(
                l.Id, l.ItemId, l.ItemName, l.PurchaseUnit, l.Quantity, l.FreeQuantity, l.Rate, l.DiscountPercent, l.GstRate, l.TaxableAmount, l.CgstAmount,
                l.SgstAmount, l.IgstAmount, l.LineTotal, l.ReceivedQuantity, l.PendingQuantity)).ToList(),
            o.Version));
    }
}
