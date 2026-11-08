using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Transfers;

/// <param name="BatchId">Send a specific batch instead of FEFO.</param>
public sealed record TransferLineInput(Guid ItemId, decimal Quantity, Guid? BatchId);

public sealed record DispatchTransferRequest(Guid FromStoreId, Guid ToStoreId, string? Remarks, IReadOnlyList<TransferLineInput> Lines);

/// <summary>A line counted in at the receiving store: only lines counted short need be sent.</summary>
public sealed record ReceivedLineInput(Guid LineId, decimal ReceivedQuantity);

/// <param name="Lines">The lines counted short; leave out to receive every line as sent.</param>
/// <param name="Note">Why it arrived short (broken, missing), for whoever decides the shortfall.</param>
public sealed record ReceiveTransferRequest(IReadOnlyList<ReceivedLineInput>? Lines, string? Note);

public sealed record TransferListItem(
    Guid Id,
    string Number,
    Guid FromStoreId,
    string FromStoreName,
    Guid ToStoreId,
    string ToStoreName,
    TransferStatus Status,
    DateTimeOffset DispatchedAt,
    DateTimeOffset? ReceivedAt,
    decimal TotalValue,
    int LineCount);

public sealed record TransferLineDto(
    Guid Id,
    Guid ItemId,
    string ItemName,
    string BatchNumber,
    DateOnly? ExpiryDate,
    decimal Quantity,
    decimal Mrp,
    decimal PurchaseRate,
    Guid SourceBatchId,
    Guid? DestinationBatchId,
    decimal? ReceivedQuantity = null);

public sealed record TransferDetail(
    Guid Id,
    string Number,
    Guid FromStoreId,
    string FromStoreName,
    Guid ToStoreId,
    string ToStoreName,
    TransferStatus Status,
    DateTimeOffset DispatchedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    string? Remarks,
    decimal TotalValue,
    IReadOnlyList<TransferLineDto> Lines,
    uint Version);

public sealed class DispatchTransferRequestValidator : AbstractValidator<DispatchTransferRequest>
{
    public DispatchTransferRequestValidator()
    {
        RuleFor(x => x.FromStoreId).NotEmpty();
        RuleFor(x => x.ToStoreId).NotEmpty().NotEqual(x => x.FromStoreId).WithMessage("Choose a different destination store.");
        RuleFor(x => x.Remarks).SafeText(500);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one item.")
            .Must(l => l.Count <= 200).WithMessage("A transfer can have at most 200 lines.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
    }
}

public sealed class ReceiveTransferRequestValidator : AbstractValidator<ReceiveTransferRequest>
{
    public ReceiveTransferRequestValidator()
    {
        RuleFor(x => x.Note).SafeText(500);
        RuleFor(x => x.Lines).Must(l => l is null || l.Count <= 200).WithMessage("A transfer has at most 200 lines.")
            .Must(l => l is null || l.Select(x => x.LineId).Distinct().Count() == l.Count).WithMessage("Count each line once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.LineId).NotEmpty();
            line.RuleFor(l => l.ReceivedQuantity).GreaterThanOrEqualTo(0);
        });
    }
}

/// <summary>/api/v1/inventory/transfers — stock transfer notes between stores (INV-06, PH-04).</summary>
internal static class TransferEndpoints
{
    private static readonly string[] Readers = [SupplyChainPermissions.Transfers, SupplyChainPermissions.StockRead];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inventory/transfers").WithTags("Inventory");

        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapPost("/", async (DispatchTransferRequest r, TransferService transfers, CancellationToken ct) =>
                (await transfers.DispatchAsync(r, ct)).ToCreated(d => $"/api/v1/inventory/transfers/{d.Id}"))
            .RequirePermission(SupplyChainPermissions.Transfers).WithValidation<DispatchTransferRequest>();
        group.MapPost("/{id:guid}/receive", Receive)
            .RequirePermission(SupplyChainPermissions.Transfers).WithValidation<ReceiveTransferRequest>();
        group.MapPost("/{id:guid}/cancel", async (Guid id, InventoryReasonRequest r, TransferService transfers, CancellationToken ct) =>
                (await transfers.CancelAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.Transfers).WithValidation<InventoryReasonRequest>();
    }

    /// <summary>Received at once (204), or 202 with the request when a line was counted short and a second person decides.</summary>
    private static async Task<IResult> Receive(Guid id, ReceiveTransferRequest request, TransferService transfers, CancellationToken ct)
    {
        var received = await transfers.ReceiveAsync(id, request, ct);
        if (received.IsFailure)
        {
            return received.Error!.ToProblem();
        }

        return received.Value is { } asked ? TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked) : TypedResults.NoContent();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, TransferStatus? status, Guid? storeId, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var transfers = db.Set<StockTransfer>().AsNoTracking();
        if (status is not null)
        {
            transfers = transfers.Where(t => t.Status == status);
        }

        if (storeId is not null)
        {
            transfers = transfers.Where(t => t.FromStoreId == storeId || t.ToStoreId == storeId);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            transfers = transfers.Where(t => t.Number == upper);
        }

        var result = await transfers.OrderByDescending(t => t.DispatchedAt)
            .Select(t => new TransferListItem(
                t.Id, t.Number, t.FromStoreId,
                db.Set<Store>().Where(s => s.Id == t.FromStoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty,
                t.ToStoreId,
                db.Set<Store>().Where(s => s.Id == t.ToStoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty,
                t.Status, t.DispatchedAt, t.ReceivedAt, t.TotalValue, t.Lines.Count))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var t = await db.Set<StockTransfer>().AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null)
        {
            return Error.NotFound(nameof(StockTransfer), id).ToProblem();
        }

        var storeIds = new[] { t.FromStoreId, t.ToStoreId };
        var stores = await db.Set<Store>().AsNoTracking().Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return TypedResults.Ok(new TransferDetail(
            t.Id, t.Number, t.FromStoreId, stores.GetValueOrDefault(t.FromStoreId) ?? string.Empty, t.ToStoreId,
            stores.GetValueOrDefault(t.ToStoreId) ?? string.Empty, t.Status, t.DispatchedAt, t.ReceivedAt, t.CancelledAt, t.CancelReason, t.Remarks,
            t.TotalValue,
            t.Lines.OrderBy(l => l.ItemName).Select(l => new TransferLineDto(
                l.Id, l.ItemId, l.ItemName, l.BatchNumber, l.ExpiryDate, l.Quantity, l.Mrp, l.PurchaseRate, l.SourceBatchId, l.DestinationBatchId,
                l.ReceivedQuantity)).ToList(),
            t.Version));
    }
}
