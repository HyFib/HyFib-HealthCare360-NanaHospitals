using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Indents;

/// <summary>/api/v1/inventory/indents — department/sub-store requests, approval with reservation, and issue (INV-05).</summary>
internal static class IndentEndpoints
{
    private static readonly string[] Readers = [SupplyChainPermissions.IndentsRaise, SupplyChainPermissions.IndentsIssue, SupplyChainPermissions.StockRead];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inventory/indents").WithTags("Inventory");

        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}/issues", Issues).RequireAnyPermission(Readers);
        group.MapPost("/", async (CreateIndentRequest r, IndentService indents, CancellationToken ct) =>
                (await indents.CreateAsync(r, ct)).ToCreated(d => $"/api/v1/inventory/indents/{d.Id}"))
            .RequirePermission(SupplyChainPermissions.IndentsRaise).WithValidation<CreateIndentRequest>();
        group.MapPost("/{id:guid}/approve", async (Guid id, ApproveIndentRequest r, IndentService indents, CancellationToken ct) =>
                (await indents.ApproveAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.IndentsIssue).WithValidation<ApproveIndentRequest>();
        group.MapPost("/{id:guid}/reject", async (Guid id, InventoryReasonRequest r, IndentService indents, CancellationToken ct) =>
                (await indents.RejectAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.IndentsIssue).WithValidation<InventoryReasonRequest>();
        group.MapPost("/{id:guid}/cancel", async (Guid id, InventoryReasonRequest r, IndentService indents, CancellationToken ct) =>
                (await indents.CancelAsync(id, r.Reason, ct)).ToNoContent())
            .RequireAnyPermission(SupplyChainPermissions.IndentsRaise, SupplyChainPermissions.IndentsIssue).WithValidation<InventoryReasonRequest>();
        group.MapPost("/{id:guid}/issue", async (Guid id, IssueIndentRequest r, IndentService indents, CancellationToken ct) =>
                (await indents.IssueAsync(id, r, ct)).ToOk())
            .RequirePermission(SupplyChainPermissions.IndentsIssue).WithValidation<IssueIndentRequest>();
        group.MapPost("/{id:guid}/close", async (Guid id, InventoryReasonRequest r, IndentService indents, CancellationToken ct) =>
                (await indents.CloseAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.IndentsIssue).WithValidation<InventoryReasonRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, IndentStatus? status, Guid? fromStoreId, Guid? toStoreId, Guid? departmentId, bool? openOnly, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var indents = db.Set<Indent>().AsNoTracking();
        if (status is not null)
        {
            indents = indents.Where(i => i.Status == status);
        }

        if (openOnly == true)
        {
            indents = indents.Where(i => i.Status == IndentStatus.Pending || i.Status == IndentStatus.Approved || i.Status == IndentStatus.PartiallyIssued);
        }

        if (fromStoreId is not null)
        {
            indents = indents.Where(i => i.FromStoreId == fromStoreId);
        }

        if (toStoreId is not null)
        {
            indents = indents.Where(i => i.ToStoreId == toStoreId);
        }

        if (departmentId is not null)
        {
            indents = indents.Where(i => i.DepartmentId == departmentId);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            indents = indents.Where(i => i.Number == upper);
        }

        // Urgent requests first, then oldest first — the store works the queue top-down. Enums are stored as text,
        // so the priority is ranked explicitly rather than sorted (which would be alphabetical in SQL).
        var result = await indents.OrderByDescending(i => i.Priority == IndentPriority.Urgent).ThenBy(i => i.RequestedAt)
            .Select(i => new IndentListItem(
                i.Id, i.Number, i.FromStoreId,
                db.Set<Store>().Where(s => s.Id == i.FromStoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty,
                i.ToStoreId,
                db.Set<Store>().Where(s => s.Id == i.ToStoreId).Select(s => s.Name).FirstOrDefault(),
                i.DepartmentId,
                db.Set<Department>().Where(d => d.Id == i.DepartmentId).Select(d => d.Name).FirstOrDefault(),
                i.Priority, i.Status, i.RequestedAt,
                db.Set<User>().Where(u => u.Id == i.RequestedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                i.Lines.Count))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, StockService stock, CancellationToken ct)
    {
        var indent = await db.Set<Indent>().AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (indent is null)
        {
            return Error.NotFound(nameof(Indent), id).ToProblem();
        }

        var storeIds = new[] { indent.FromStoreId, indent.ToStoreId ?? Guid.Empty };
        var stores = await db.Set<Store>().AsNoTracking().Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var department = indent.DepartmentId is { } departmentId
            ? await db.Set<Department>().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;
        var requestedBy = await db.Set<User>().Where(u => u.Id == indent.RequestedByUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        var reserved = await db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.ReferenceModule == IndentService.ReferenceModule && r.ReferenceId == id && r.Status == ReservationStatus.Active)
            .GroupBy(r => r.ReferenceLineId)
            .Select(g => new { LineId = g.Key, Quantity = g.Sum(r => r.Quantity - r.ConsumedQuantity - r.ReleasedQuantity) })
            .ToListAsync(ct);
        var available = await stock.GetAvailableAsync(indent.FromStoreId, indent.Lines.Select(l => l.ItemId).ToList(), ct);

        return TypedResults.Ok(new IndentDetail(
            indent.Id, indent.Number, indent.FromStoreId, stores.GetValueOrDefault(indent.FromStoreId) ?? string.Empty, indent.ToStoreId,
            indent.ToStoreId is { } to ? stores.GetValueOrDefault(to) : null, indent.DepartmentId, department, indent.Priority, indent.Status,
            indent.RequestedAt, requestedBy, indent.Remarks, indent.DecidedAt, indent.DecisionNote,
            indent.Lines.OrderBy(l => l.ItemName).Select(l => new IndentLineDto(
                l.Id, l.ItemId, l.ItemName, l.RequestedQuantity, l.ApprovedQuantity, l.IssuedQuantity, l.PendingQuantity,
                reserved.Where(r => r.LineId == l.Id).Sum(r => r.Quantity), available.GetValueOrDefault(l.ItemId), l.Remarks)).ToList(),
            indent.Version));
    }

    private static async Task<IResult> Issues(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var issues = await db.Set<IndentIssue>().AsNoTracking().Include(i => i.Lines)
            .Where(i => i.IndentId == id)
            .OrderBy(i => i.IssuedAt)
            .ToListAsync(ct);
        var userIds = issues.Select(i => i.IssuedByUserId).OfType<Guid>().Distinct().ToList();
        var users = await db.Set<User>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return TypedResults.Ok(issues.Select(i => new IndentIssueDto(
            i.Id, i.Number, i.IndentId, i.IssuedAt, i.IssuedByUserId is { } by ? users.GetValueOrDefault(by) : null, i.ReceivedByName, i.Remarks,
            i.TotalValue,
            i.Lines.Select(l => new IndentIssueLineDto(l.ItemId, l.ItemName, l.BatchNumber, l.ExpiryDate, l.Quantity, l.UnitCost, l.Value)).ToList())).ToList());
    }
}
