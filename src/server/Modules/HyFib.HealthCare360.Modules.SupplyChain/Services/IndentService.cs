using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Indents;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Indents and issues (INV-05). Approval reserves stock (FEFO, what is available) so it cannot be taken by other
/// issues; issuing consumes those reservations first. An indent from a sub-store moves stock as a transfer, an
/// indent from a department books it as consumed. Cancelling, rejecting or closing releases the reservations.
/// </summary>
public sealed class IndentService(HospitalDbContext db, INumberSeries numbers, StockService stock, ICurrentUser currentUser, TimeProvider time)
{
    public const string ReferenceModule = "supplychain.indent";

    public async Task<Result<DocumentCreated>> CreateAsync(CreateIndentRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Store>().AnyAsync(s => s.Id == request.FromStoreId && s.IsActive, ct))
        {
            return Error.Field("fromStoreId", "Choose an active issuing store.");
        }

        if (request.ToStoreId is { } toStoreId && !await db.Set<Store>().AnyAsync(s => s.Id == toStoreId && s.IsActive, ct))
        {
            return Error.Field("toStoreId", "Choose an active receiving store.");
        }

        if (request.DepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId, ct))
        {
            return Error.Field("departmentId", "Choose a department of this facility.");
        }

        var itemIds = request.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Set<Item>().AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.IsActive).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        if (items.Count != itemIds.Count)
        {
            return Error.Field("lines", "One of the items is unknown or inactive.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var indent = new Indent
        {
            FacilityId = facilityId,
            Number = await numbers.NextAsync(SupplyChainNumberSeries.Indent, facilityId, ct),
            FromStoreId = request.FromStoreId,
            ToStoreId = request.ToStoreId,
            DepartmentId = request.DepartmentId,
            Priority = request.Priority,
            RequestedByUserId = currentUser.UserId,
            RequestedAt = time.GetUtcNow(),
            Remarks = request.Remarks?.Trim(),
            Lines = request.Lines.Select(l => new IndentLine
            {
                ItemId = l.ItemId,
                ItemName = items[l.ItemId],
                RequestedQuantity = l.Quantity,
                Remarks = l.Remarks?.Trim(),
            }).ToList(),
        };
        db.Add(indent);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DocumentCreated(indent.Id, indent.Number);
    }

    public async Task<Result> ApproveAsync(Guid id, ApproveIndentRequest request, CancellationToken ct)
    {
        var indent = await LoadAsync(id, ct);
        if (indent is null)
        {
            return Error.NotFound(nameof(Indent), id);
        }

        var approved = (request.Lines ?? []).ToDictionary(l => l.LineId, l => l.ApprovedQuantity);
        var result = indent.Approve(currentUser.UserId, approved, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reference = new StockReference(ReferenceModule, indent.Id, indent.Number);
        foreach (var line in indent.Lines.Where(l => l.ApprovedQuantity > 0))
        {
            // Reserve what is on the shelf now; the rest is issued later as stock arrives (no reservation).
            var reserved = await stock.ReserveAsync(indent.FromStoreId, line.ItemId, line.ApprovedQuantity!.Value, reference, line.Id, allowPartial: true, ct);
            if (reserved.IsFailure)
            {
                return reserved;
            }
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved;
        }

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    public Task<Result> RejectAsync(Guid id, string reason, CancellationToken ct) =>
        CloseWithReleaseAsync(id, indent => indent.Reject(currentUser.UserId, reason.Trim(), time.GetUtcNow()), ct);

    public Task<Result> CancelAsync(Guid id, string reason, CancellationToken ct) =>
        CloseWithReleaseAsync(id, indent => indent.Cancel(reason.Trim(), time.GetUtcNow()), ct);

    public Task<Result> CloseAsync(Guid id, string reason, CancellationToken ct) =>
        CloseWithReleaseAsync(id, indent => indent.Close(reason.Trim(), time.GetUtcNow()), ct);

    public async Task<Result<DocumentCreated>> IssueAsync(Guid id, IssueIndentRequest request, CancellationToken ct)
    {
        var indent = await LoadAsync(id, ct);
        if (indent is null)
        {
            return Error.NotFound(nameof(Indent), id);
        }

        if (!indent.IsOpen)
        {
            return Error.Rule("indent.closed", $"Indent {indent.Number} is {indent.Status}.");
        }

        var lines = new List<(IndentLine Line, IndentIssueLineInput Input)>();
        foreach (var input in request.Lines)
        {
            var line = indent.Lines.FirstOrDefault(l => l.Id == input.LineId);
            if (line is null)
            {
                return Error.NotFound(nameof(IndentLine), input.LineId);
            }

            if (input.Quantity > line.PendingQuantity)
            {
                return Error.Rule("indent.over_issue", $"{line.ItemName}: only {StockErrors.Format(line.PendingQuantity)} is pending on this indent.");
            }

            lines.Add((line, input));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var issueNumber = await numbers.NextAsync(SupplyChainNumberSeries.Issue, indent.FacilityId, ct);
        var reference = new StockReference(ReferenceModule, indent.Id, issueNumber, $"Indent {indent.Number}");
        var demands = lines.Select(l => new StockDemand(l.Line.ItemId, l.Input.Quantity, l.Input.BatchId, l.Line.Id)).ToList();

        IReadOnlyList<StockMovement> movements;
        if (indent.ToStoreId is { } toStoreId)
        {
            var moved = await stock.TransferAsync(indent.FromStoreId, toStoreId, demands, reference, consumeReservations: true, ct);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }

            movements = moved.Value.Where(m => m.StoreId == indent.FromStoreId).ToList();
        }
        else
        {
            var issued = await stock.IssueAsync(new StockIssueRequest(indent.FromStoreId, StockMovementType.Issue, reference, demands, ConsumeReservations: true), ct);
            if (issued.IsFailure)
            {
                return issued.Error!;
            }

            movements = issued.Value.Movements;
        }

        foreach (var (line, input) in lines)
        {
            var recorded = indent.RecordIssue(line.Id, input.Quantity, now);
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }
        }

        var slip = new IndentIssue
        {
            FacilityId = indent.FacilityId,
            Number = issueNumber,
            IndentId = indent.Id,
            FromStoreId = indent.FromStoreId,
            ToStoreId = indent.ToStoreId,
            DepartmentId = indent.DepartmentId,
            IssuedAt = now,
            IssuedByUserId = currentUser.UserId,
            ReceivedByName = request.ReceivedByName?.Trim(),
            Remarks = request.Remarks?.Trim(),
            Lines = movements.Select(m => new IndentIssueLine
            {
                IndentLineId = m.LineId ?? Guid.Empty,
                ItemId = m.ItemId,
                ItemName = m.ItemName,
                BatchId = m.BatchId,
                BatchNumber = m.BatchNumber,
                ExpiryDate = m.ExpiryDate,
                Quantity = m.Quantity,
                UnitCost = m.UnitCost,
                Value = Money.Round(m.Quantity * m.UnitCost),
            }).ToList(),
        };
        slip.TotalValue = slip.Lines.Sum(l => l.Value);
        db.Add(slip);

        if (!indent.IsOpen)
        {
            var released = await stock.ReleaseReservationsAsync(ReferenceModule, indent.Id, null, ct);
            if (released.IsFailure)
            {
                return released.Error!;
            }
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return new DocumentCreated(slip.Id, slip.Number);
    }

    private async Task<Result> CloseWithReleaseAsync(Guid id, Func<Indent, Result> change, CancellationToken ct)
    {
        var indent = await LoadAsync(id, ct);
        if (indent is null)
        {
            return Error.NotFound(nameof(Indent), id);
        }

        var result = change(indent);
        if (result.IsFailure)
        {
            return result;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var released = await stock.ReleaseReservationsAsync(ReferenceModule, indent.Id, null, ct);
        if (released.IsFailure)
        {
            return released;
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved;
        }

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    private Task<Indent?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Set<Indent>().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct);
}
