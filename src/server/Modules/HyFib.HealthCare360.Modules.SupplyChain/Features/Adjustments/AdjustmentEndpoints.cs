using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Adjustments;

/// <summary>/api/v1/inventory/adjustments and /counts — approved stock corrections and physical stock counts (INV-07).</summary>
internal static class AdjustmentEndpoints
{
    private static readonly string[] Raisers = [SupplyChainPermissions.Counts, SupplyChainPermissions.AdjustmentsApprove];
    private static readonly string[] Readers = [SupplyChainPermissions.Counts, SupplyChainPermissions.AdjustmentsApprove, SupplyChainPermissions.StockRead];

    public static void Map(IEndpointRouteBuilder app)
    {
        var adjustments = app.MapGroup("/api/v1/inventory/adjustments").WithTags("Inventory");
        adjustments.MapGet("/", ListAdjustments).RequireAnyPermission(Readers);
        adjustments.MapGet("/{id:guid}", GetAdjustment).RequireAnyPermission(Readers);
        adjustments.MapPost("/", async (CreateAdjustmentRequest r, AdjustmentService service, CancellationToken ct) =>
                (await service.CreateAsync(r, ct)).ToCreated(d => $"/api/v1/inventory/adjustments/{d.Id}"))
            .RequireAnyPermission(Raisers).WithValidation<CreateAdjustmentRequest>();
        adjustments.MapPost("/expired-write-off", async (Guid storeId, AdjustmentService service, CancellationToken ct) =>
                (await service.CreateExpiredWriteOffAsync(storeId, ct)).ToCreated(d => $"/api/v1/inventory/adjustments/{d.Id}"))
            .RequireAnyPermission(Raisers);
        adjustments.MapPost("/{id:guid}/approve", async (Guid id, AdjustmentService service, CancellationToken ct) =>
                (await service.ApproveAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AdjustmentsApprove);
        adjustments.MapPost("/{id:guid}/reject", async (Guid id, InventoryReasonRequest r, AdjustmentService service, CancellationToken ct) =>
                (await service.RejectAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AdjustmentsApprove).WithValidation<InventoryReasonRequest>();

        var counts = app.MapGroup("/api/v1/inventory/counts").WithTags("Inventory");
        counts.MapGet("/", ListCounts).RequireAnyPermission(Readers);
        counts.MapGet("/{id:guid}", GetCount).RequireAnyPermission(Readers);
        counts.MapPost("/", async (StartCountRequest r, AdjustmentService service, CancellationToken ct) =>
                (await service.StartCountAsync(r, ct)).ToCreated(d => $"/api/v1/inventory/counts/{d.Id}"))
            .RequirePermission(SupplyChainPermissions.Counts).WithValidation<StartCountRequest>();
        counts.MapPut("/{id:guid}/lines", async (Guid id, RecordCountsRequest r, AdjustmentService service, CancellationToken ct) =>
                (await service.RecordCountsAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.Counts).WithValidation<RecordCountsRequest>();
        counts.MapPost("/{id:guid}/submit", async (Guid id, AdjustmentService service, CancellationToken ct) =>
                (await service.SubmitCountAsync(id, ct)).ToHttpResult(adjustmentId => TypedResults.Ok(new { adjustmentId })))
            .RequirePermission(SupplyChainPermissions.Counts);
        counts.MapPost("/{id:guid}/cancel", async (Guid id, AdjustmentService service, CancellationToken ct) =>
                (await service.CancelCountAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.Counts);
    }

    private static async Task<IResult> ListAdjustments(
        [AsParameters] PageQuery query, AdjustmentStatus? status, Guid? storeId, AdjustmentReason? reason, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var adjustments = db.Set<StockAdjustment>().AsNoTracking();
        if (status is not null)
        {
            adjustments = adjustments.Where(a => a.Status == status);
        }

        if (storeId is not null)
        {
            adjustments = adjustments.Where(a => a.StoreId == storeId);
        }

        if (reason is not null)
        {
            adjustments = adjustments.Where(a => a.Reason == reason);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            adjustments = adjustments.Where(a => a.Number == upper);
        }

        var result = await adjustments.OrderByDescending(a => a.RequestedAt)
            .Select(a => new AdjustmentListItem(
                a.Id, a.Number, a.StoreId,
                db.Set<Store>().Where(s => s.Id == a.StoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty,
                a.Reason, a.Status, a.NetValue, a.RequestedAt,
                db.Set<User>().Where(u => u.Id == a.RequestedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                a.Lines.Count))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetAdjustment(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var a = await db.Set<StockAdjustment>().AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            return Error.NotFound(nameof(StockAdjustment), id).ToProblem();
        }

        var storeName = await db.Set<Store>().Where(s => s.Id == a.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var userIds = new[] { a.RequestedByUserId, a.DecidedByUserId }.OfType<Guid>().ToList();
        var users = await db.Set<User>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return TypedResults.Ok(new AdjustmentDetail(
            a.Id, a.Number, a.StoreId, storeName, a.Reason, a.Status, a.Remarks, a.NetValue, a.RequestedAt,
            a.RequestedByUserId is { } by ? users.GetValueOrDefault(by) : null, a.DecidedAt,
            a.DecidedByUserId is { } decidedBy ? users.GetValueOrDefault(decidedBy) : null, a.RejectReason, a.PhysicalCountId,
            a.Lines.OrderBy(l => l.ItemName).Select(l => new AdjustmentLineDto(
                l.Id, l.ItemId, l.ItemName, l.BatchId, l.BatchNumber, l.ExpiryDate, l.SystemQuantity, l.QuantityChange, l.UnitCost, l.Value)).ToList(),
            a.Version));
    }

    private static async Task<IResult> ListCounts([AsParameters] PageQuery query, Guid? storeId, StockCountStatus? status, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var counts = db.Set<PhysicalStockCount>().AsNoTracking();
        if (storeId is not null)
        {
            counts = counts.Where(c => c.StoreId == storeId);
        }

        if (status is not null)
        {
            counts = counts.Where(c => c.Status == status);
        }

        var result = await counts.OrderByDescending(c => c.StartedAt)
            .Select(c => new CountListItem(
                c.Id, c.Number, c.StoreId,
                db.Set<Store>().Where(s => s.Id == c.StoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty,
                c.Status, c.StartedAt, c.SubmittedAt, c.Lines.Count, c.Lines.Count(l => l.CountedQuantity != null), c.AdjustmentId))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetCount(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var c = await db.Set<PhysicalStockCount>().AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound(nameof(PhysicalStockCount), id).ToProblem();
        }

        var storeName = await db.Set<Store>().Where(s => s.Id == c.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        return TypedResults.Ok(new CountDetail(
            c.Id, c.Number, c.StoreId, storeName, c.Status, c.StartedAt, c.SubmittedAt, c.ItemTypeFilter, c.CategoryFilterId, c.Remarks, c.AdjustmentId,
            c.Lines.OrderBy(l => l.ItemName).ThenBy(l => l.ExpiryDate).Select(l => new CountLineDto(
                l.Id, l.ItemId, l.ItemName, l.BatchId, l.BatchNumber, l.ExpiryDate, l.SystemQuantity, l.CountedQuantity, l.VarianceQuantity,
                l.UnitCost, l.Remarks)).ToList(),
            c.Version));
    }
}
