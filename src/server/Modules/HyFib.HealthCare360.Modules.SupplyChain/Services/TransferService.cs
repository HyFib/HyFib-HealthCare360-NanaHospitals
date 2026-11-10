using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Transfers;
using System.Globalization;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Two-step stock transfers between stores (INV-06, PH-04): dispatch (transfer-out, goods in transit) and receipt
/// (transfer-in with the same batch, expiry and MRP). Cancelling an in-transit transfer returns the stock to the
/// exact source batches.
/// </summary>
public sealed class TransferService(
    HospitalDbContext db, INumberSeries numbers, StockService stock, ICurrentUser currentUser, TimeProvider time, IApprovals approvals, IAuditLogger audit)
{
    public const string ReferenceModule = "supplychain.transfer";

    public async Task<Result<DocumentCreated>> DispatchAsync(DispatchTransferRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var storeIds = new[] { request.FromStoreId, request.ToStoreId };
        if (await db.Set<Store>().CountAsync(s => storeIds.Contains(s.Id) && s.IsActive, ct) != 2)
        {
            return Error.Field("toStoreId", "Choose two different active stores of this facility.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var number = await numbers.NextAsync(SupplyChainNumberSeries.Transfer, facilityId, ct);
        var transfer = new StockTransfer
        {
            FacilityId = facilityId,
            Number = number,
            FromStoreId = request.FromStoreId,
            ToStoreId = request.ToStoreId,
            DispatchedAt = time.GetUtcNow(),
            DispatchedByUserId = currentUser.UserId,
            Remarks = request.Remarks?.Trim(),
        };

        var issued = await stock.IssueAsync(
            new StockIssueRequest(
                request.FromStoreId,
                StockMovementType.TransferOut,
                new StockReference(ReferenceModule, transfer.Id, number),
                request.Lines.Select(l => new StockDemand(l.ItemId, l.Quantity, l.BatchId)).ToList(),
                CounterpartStoreId: request.ToStoreId),
            ct);
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        foreach (var moved in issued.Value.Movements)
        {
            var source = db.Set<StockBatch>().Local.First(b => b.Id == moved.BatchId);
            transfer.Lines.Add(new StockTransferLine
            {
                StockTransferId = transfer.Id,
                ItemId = moved.ItemId,
                ItemName = moved.ItemName,
                SourceBatchId = moved.BatchId,
                BatchNumber = moved.BatchNumber,
                ExpiryDate = moved.ExpiryDate,
                ManufactureDate = source.ManufactureDate,
                Mrp = moved.Mrp,
                PurchaseRate = moved.UnitCost,
                GstRate = moved.GstRate,
                Quantity = moved.Quantity,
            });
        }

        transfer.TotalValue = Money.Round(transfer.Lines.Sum(l => l.Quantity * l.PurchaseRate));
        db.Add(transfer);
        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return new DocumentCreated(transfer.Id, transfer.Number);
    }

    /// <summary>
    /// Receives a transfer at the destination store. Someone other than whoever dispatched it receives it. Every line
    /// counted in full: the goods are booked in at once. A line counted short: nothing moves yet and a second person
    /// (<see cref="SupplyChainPermissions.AdjustmentsApprove"/>, neither the receiver nor the dispatcher) decides the
    /// shortfall (<see cref="ShortfallKind"/>), since the difference leaves the books as a loss.
    /// </summary>
    public async Task<Result<ApprovalRequestDto?>> ReceiveAsync(Guid id, ReceiveTransferRequest? request, CancellationToken ct)
    {
        var transfer = await db.Set<StockTransfer>().Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (transfer is null)
        {
            return Error.NotFound(nameof(StockTransfer), id);
        }

        var allowed = transfer.CanBeReceivedBy(currentUser.UserId);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var counted = Counted(transfer, request?.Lines);
        if (counted.IsFailure)
        {
            return counted.Error!;
        }

        if (counted.Value.Count == 0)
        {
            var received = await ReceiveCoreAsync(transfer, currentUser.UserId, counted.Value, null, ct);
            return received.IsFailure ? received.Error! : (ApprovalRequestDto?)null;
        }

        var asked = await AskShortfallAsync(transfer, counted.Value, request?.Note, ct);
        return asked.IsFailure ? asked.Error! : asked.Value;
    }

    public const string ShortfallKind = "inventory.transfer_shortfall";

    public const string OverReceivedCode = "transfer.over_received";

    /// <summary>The lines counted short, by line, checked against what was sent.</summary>
    private static Result<IReadOnlyDictionary<Guid, decimal>> Counted(StockTransfer transfer, IReadOnlyList<ReceivedLineInput>? lines)
    {
        var shortLines = new Dictionary<Guid, decimal>();
        foreach (var input in lines ?? [])
        {
            var line = transfer.Lines.FirstOrDefault(l => l.Id == input.LineId);
            if (line is null)
            {
                return Error.Field("lines", $"A line counted in is not on transfer {transfer.Number}.");
            }

            if (input.ReceivedQuantity > line.Quantity)
            {
                return Error.Rule(OverReceivedCode,
                    $"{line.ItemName} batch {line.BatchNumber}: {Qty(input.ReceivedQuantity)} counted in but only {Qty(line.Quantity)} was sent. Check the count; more than was sent cannot be received on a transfer.");
            }

            if (input.ReceivedQuantity < line.Quantity)
            {
                shortLines[line.Id] = input.ReceivedQuantity;
            }
        }

        return shortLines;
    }

    private async Task<Result<ApprovalRequestDto>> AskShortfallAsync(
        StockTransfer transfer, IReadOnlyDictionary<Guid, decimal> counted, string? note, CancellationToken ct)
    {
        var stores = await StoreNamesAsync(transfer, ct);
        var shortLines = transfer.Lines.Where(l => counted.ContainsKey(l.Id)).OrderBy(l => l.ItemName).ToList();
        var loss = Money.Round(shortLines.Sum(l => (l.Quantity - counted[l.Id]) * l.PurchaseRate));
        var dispatcher = await UserNameAsync(transfer.DispatchedByUserId, ct);
        List<ApprovalDetail> details =
        [
            new("Transfer", $"{transfer.Number}, {stores.From} to {stores.To}"),
            new("Dispatched by", dispatcher),
            .. shortLines.Select(l => new ApprovalDetail(
                $"{l.ItemName} batch {l.BatchNumber}",
                $"sent {Qty(l.Quantity)}, counted in {Qty(counted[l.Id])}: {Qty(l.Quantity - counted[l.Id])} short")),
            new("What it changes",
                $"The receiving store books in what was sent and {Qty(shortLines.Sum(l => l.Quantity - counted[l.Id]))} is written off from it as short on receipt, worth {DocumentFormat.Money(loss)} at purchase rate. The other lines are booked in as sent."),
        ];
        var payload = new TransferShortfallPayload(transfer.Id, counted.Select(c => new ReceivedLineInput(c.Key, c.Value)).ToList(), note?.Trim());
        return await approvals.RequestAsync(
            ShortfallKind,
            new ApprovalSubject(nameof(StockTransfer), transfer.Id, transfer.Version, transfer.FacilityId, null),
            payload,
            $"Transfer {transfer.Number} ({stores.From} to {stores.To}): {shortLines.Count} line(s) short on receipt, {DocumentFormat.Money(loss)}",
            details,
            string.IsNullOrWhiteSpace(note) ? "Counted short on receipt." : note.Trim(),
            ct);
    }

    /// <summary>
    /// Receives a transfer whose shortfall was approved, in the approval's transaction (the approval saves): as counted
    /// when it was asked, or not at all. The dispatcher may not decide it either.
    /// </summary>
    internal async Task<Result<string>> ApplyShortfallAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<TransferShortfallPayload>(request.Payload);
        var transfer = await db.Set<StockTransfer>().Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == asked.TransferId, ct);
        if (transfer is null)
        {
            return Error.NotFound(nameof(StockTransfer), asked.TransferId);
        }

        if (transfer.IsDispatcher(currentUser.UserId))
        {
            return Error.Forbidden(StockTransfer.ShortfallOwnCode,
                $"You dispatched transfer {transfer.Number}: someone other than the sender decides what arrived short.");
        }

        if (transfer.Status != TransferStatus.InTransit || transfer.Version != request.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"Transfer {transfer.Number} changed after its shortfall was asked about (it is {transfer.Status} now), so nothing was received.");
        }

        var counted = asked.Lines.ToDictionary(l => l.LineId, l => l.ReceivedQuantity);
        var received = await ReceiveCoreAsync(transfer, request.RequestedBy, counted, request.RequestedByName, ct);
        if (received.IsFailure)
        {
            return received.Error!;
        }

        var shortLines = transfer.Lines.Where(l => counted.ContainsKey(l.Id)).OrderBy(l => l.ItemName).ToList();
        var words = string.Join("; ", shortLines.Select(l => $"{l.ItemName} batch {l.BatchNumber}: sent {Qty(l.Quantity)}, received {Qty(counted[l.Id])}"));
        var loss = Money.Round(shortLines.Sum(l => (l.Quantity - counted[l.Id]) * l.PurchaseRate));
        var approver = currentUser.DisplayName ?? currentUser.UserName;
        audit.Add(new AuditEntry(AuditCategories.Admin, "inventory.transfer_shortfall_accepted",
            Fit($"Transfer {transfer.Number} received short ({words}), {DocumentFormat.Money(loss)} written off; counted in by {request.RequestedByName}, approved by {approver}", 1000))
        {
            EntityType = nameof(StockTransfer),
            EntityId = transfer.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, receivedBy = request.RequestedBy, lines = asked.Lines, loss },
        });
        return $"Transfer {transfer.Number} received, {DocumentFormat.Money(loss)} written off as short on receipt";
    }

    /// <summary>
    /// Books every line in at the destination as sent, then writes the counted shortfall off the destination batch, so
    /// the ledger shows what was sent, what arrived and the loss. Saves only when not inside an approval.
    /// </summary>
    private async Task<Result> ReceiveCoreAsync(
        StockTransfer transfer, Guid? receiverId, IReadOnlyDictionary<Guid, decimal> counted, string? receiverName, CancellationToken ct)
    {
        var received = transfer.Receive(receiverId, time.GetUtcNow());
        if (received.IsFailure)
        {
            return received;
        }

        var inApproval = db.Database.CurrentTransaction is not null;
        await using var transaction = inApproval ? null : await db.Database.BeginTransactionAsync(ct);
        var reference = new StockReference(ReferenceModule, transfer.Id, transfer.Number);
        foreach (var line in transfer.Lines)
        {
            var moved = await stock.ReceiveAsync(
                new StockReceipt(transfer.ToStoreId, line.ItemId, line.BatchNumber, line.ExpiryDate, line.ManufactureDate, line.Quantity, line.Mrp,
                    line.PurchaseRate, line.GstRate),
                StockMovementType.TransferIn, reference, transfer.FromStoreId, ct);
            if (moved.IsFailure)
            {
                return moved;
            }

            line.DestinationBatchId = moved.Value.BatchId;
            if (counted.TryGetValue(line.Id, out var arrived))
            {
                line.ReceivedQuantity = arrived;
                var written = await stock.AdjustBatchAsync(moved.Value.BatchId, arrived - line.Quantity, StockMovementType.Adjustment,
                    new StockReference(ReferenceModule, transfer.Id, transfer.Number, Fit($"Short on receipt of transfer {transfer.Number}, counted by {receiverName}", 200)), ct);
                if (written.IsFailure)
                {
                    return written;
                }
            }
        }

        if (inApproval)
        {
            return Result.Success();
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved;
        }

        await transaction!.CommitAsync(ct);
        return Result.Success();
    }

    private async Task<(string From, string To)> StoreNamesAsync(StockTransfer transfer, CancellationToken ct)
    {
        var ids = new[] { transfer.FromStoreId, transfer.ToStoreId };
        var names = await db.Set<Store>().AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return (names.GetValueOrDefault(transfer.FromStoreId) ?? string.Empty, names.GetValueOrDefault(transfer.ToStoreId) ?? string.Empty);
    }

    private async Task<string> UserNameAsync(Guid? userId, CancellationToken ct) =>
        userId is { } id
            ? await db.Set<User>().AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "Unknown"
            : "Unknown";

    private static string Qty(decimal quantity) => quantity.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    public async Task<Result> CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var transfer = await db.Set<StockTransfer>().Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (transfer is null)
        {
            return Error.NotFound(nameof(StockTransfer), id);
        }

        var cancelled = transfer.Cancel(reason.Trim(), time.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reference = new StockReference(ReferenceModule, transfer.Id, transfer.Number, "Transfer cancelled: stock returned to source");
        foreach (var line in transfer.Lines)
        {
            var returned = await stock.ReturnToBatchAsync(line.SourceBatchId, line.Quantity, StockMovementType.TransferIn, reference, transfer.ToStoreId, ct);
            if (returned.IsFailure)
            {
                return returned;
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
}

/// <summary>A transfer counted short on receipt: the lines counted short and what was counted in.</summary>
public sealed record TransferShortfallPayload(Guid TransferId, IReadOnlyList<ReceivedLineInput> Lines, string? Note);

/// <summary>A transfer counted short on receipt: the receiver counts, someone else who approves adjustments (not the sender) decides.</summary>
internal sealed class TransferShortfallApproval(TransferService transfers) : IApprovalAction
{
    public string Kind => TransferService.ShortfallKind;

    public string Title => "Transfer received short";

    public string ApprovePermission => SupplyChainPermissions.AdjustmentsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => transfers.ApplyShortfallAsync(request, ct);
}
