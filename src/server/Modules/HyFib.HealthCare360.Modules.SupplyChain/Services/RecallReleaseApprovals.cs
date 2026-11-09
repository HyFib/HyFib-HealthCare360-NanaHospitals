using System.Globalization;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>A recalled batch asked to be released: the batch and the recall it was held for.</summary>
public sealed record RecallReleasePayload(Guid BatchId, string RecallReason);

/// <summary>
/// A batch held on a recall goes back into use only when two people agree (maker-checker, MAKER-CHECKER.md slice 13):
/// whoever keeps the store asks, and someone else who may release recalls (<see cref="SupplyChainPermissions.RecallRelease"/>,
/// the pharmacist or the administrator) decides. Until then the batch stays held: nothing is sold, dispensed or issued from it.
/// Holding a batch, or marking a hold as a recall, applies at once: it only makes the control stricter.
/// </summary>
public sealed class RecallReleaseApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals)
{
    public const string Kind = "inventory.recall_release";

    /// <summary>Asks a second person to release <paramref name="batch"/>, in the caller's transaction if any.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(StockBatch batch, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var words = await WordsAsync(batch, ct);
        List<ApprovalDetail> details =
        [
            new("Batch", words.Batch),
            new("Store", words.Store),
            new("On hand", words.OnHand),
            new("Now", $"Held on recall: {batch.BlockReason}"),
            new("Asked for", "Released for use"),
            new("What it changes", $"{words.OnHand} of batch {batch.BatchNumber} can be sold, dispensed and issued to patients again."),
        ];
        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(StockBatch), batch.Id, batch.Version, batch.FacilityId, null),
            new RecallReleasePayload(batch.Id, batch.BlockReason ?? string.Empty),
            Fit($"{words.Batch} in {words.Store}: recalled ({batch.BlockReason}) to released for use", 300),
            details,
            string.IsNullOrWhiteSpace(reason) ? "No reason given." : reason.Trim(),
            ct);
    }

    /// <summary>Releases the batch, in the approval's transaction, if it is still held on the recall the request was asked about.</summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<RecallReleasePayload>(request.Payload);
        var batch = await db.Set<StockBatch>().FirstOrDefaultAsync(b => b.Id == asked.BatchId, ct);
        if (batch is null)
        {
            return Error.NotFound(nameof(StockBatch), asked.BatchId);
        }

        // Stock moving in or out of the batch since does not stand in the way; the hold changing (released, held again
        // for another reason) does.
        if (!batch.IsRecalled || batch.BlockReason != asked.RecallReason)
        {
            return Error.Rule("approval.stale",
                $"Batch {batch.BatchNumber}'s hold changed after its release was asked for, so nothing was changed. Ask again, for the batch as it is now.");
        }

        var words = await WordsAsync(batch, ct);
        batch.Unblock();
        var text = $"{words.Batch} in {words.Store} ({words.OnHand} on hand): recalled ({asked.RecallReason}) to released for use";
        audit.Add(new AuditEntry(AuditCategories.Data, "stock_batch.recall_released",
            Fit($"{text}, asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName}", 1000))
        {
            EntityType = nameof(StockBatch),
            EntityId = batch.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, requestedBy = request.RequestedBy, recall = asked.RecallReason },
        });
        return text;
    }

    private async Task<(string Batch, string Store, string OnHand)> WordsAsync(StockBatch batch, CancellationToken ct)
    {
        var item = await db.Set<Item>().AsNoTracking().Where(i => i.Id == batch.ItemId).Select(i => new { i.Code, i.Name, i.BaseUnit }).FirstOrDefaultAsync(ct);
        var store = await db.Set<Store>().AsNoTracking().Where(s => s.Id == batch.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        return ($"{item?.Code} {item?.Name} batch {batch.BatchNumber}", store,
            $"{batch.QuantityOnHand.ToString("0.###", CultureInfo.InvariantCulture)} {item?.BaseUnit}".TrimEnd());
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A recalled batch released for use: the store asks, a pharmacist or administrator who did not ask decides.</summary>
internal sealed class RecallReleaseApproval(RecallReleaseApprovals recalls) : IApprovalAction
{
    public string Kind => RecallReleaseApprovals.Kind;

    public string Title => "Recalled batch released";

    public string ApprovePermission => SupplyChainPermissions.RecallRelease;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => recalls.ApplyAsync(request, ct);
}
