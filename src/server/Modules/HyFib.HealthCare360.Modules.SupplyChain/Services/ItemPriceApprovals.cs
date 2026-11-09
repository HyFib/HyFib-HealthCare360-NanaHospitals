using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>An item's pricing asked for: the item, the pricing it replaces, and the pricing asked for.</summary>
public sealed record ItemPricePayload(Guid ItemId, ItemPricing Replaced, ItemPricing Asked);

/// <summary>An item's pricing waiting for a second person.</summary>
public sealed record PendingItemPricing(Guid ApprovalId, ItemPricing Asked, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// An item's NPPA ceiling price raised or removed, or its GST rate changed, is made only when two people agree
/// (maker-checker, MAKER-CHECKER.md slice 13): whoever keeps the item master asks, and someone else who approves prices
/// (<see cref="PlatformPermissions.PricesApprove"/>, as for every other price master) decides. The rest of the item is saved
/// at once and the item keeps its pricing until then. A ceiling set or lowered applies at once (<see cref="ItemPricing"/>):
/// it only makes the control stricter.
/// </summary>
public sealed class ItemPriceApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "inventory.item_price";

    /// <summary>A save asked for other pricing while one waits for approval.</summary>
    public const string WaitingCode = "inventory.item_price_waiting";

    private const string NoReason = "No reason given.";

    /// <summary>Asks a second person to set <paramref name="item"/>'s pricing to <paramref name="asked"/>, in the caller's transaction.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(Item item, ItemPricing replaced, ItemPricing asked, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(asked);
        List<ApprovalDetail> details =
        [
            new("Item", $"{item.Code} {item.Name}"),
            new("Now", replaced.Words(item)),
            new("Asked for", asked.Words(item)),
            new("What it changes", Consequences(item, replaced, asked)),
        ];
        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Item), item.Id, item.Version, await FacilityAsync(ct), null),
            new ItemPricePayload(item.Id, replaced, asked),
            Fit($"{item.Code} {item.Name}: {asked.Words(item)} (now {replaced.Words(item)})", 300),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>The pricing waiting for approval for this item, if any.</summary>
    public async Task<PendingItemPricing?> PendingAsync(Guid itemId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Item) && r.SubjectId == itemId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Payload, r.RequestedByName, r.RequestedAt })
            .FirstOrDefaultAsync(ct);
        return request is null
            ? null
            : new PendingItemPricing(request.Id, ApprovalPayload.Read<ItemPricePayload>(request.Payload).Asked, request.RequestedByName, request.RequestedAt);
    }

    /// <summary>Sets approved pricing, in the approval's transaction, if the item still has the pricing the request replaces.</summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ItemPricePayload>(request.Payload);
        var item = await db.Set<Item>().FirstOrDefaultAsync(i => i.Id == asked.ItemId, ct);
        if (item is null)
        {
            return Error.NotFound(nameof(Item), asked.ItemId);
        }

        // Only the pricing is compared: a later change of the item's reorder level does not stand in the way, a change of
        // its pricing (a ceiling lowered at once since) does.
        var before = ItemPricing.Of(item);
        if (before != asked.Replaced)
        {
            return Error.Rule("approval.stale",
                $"{item.Code} {item.Name}'s ceiling price or GST rate changed after this was asked for, so nothing was changed. Ask again, for the item as it is now.");
        }

        var wordsBefore = before.Words(item);
        asked.Asked.ApplyTo(item);
        var valid = item.Validate();
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        var words = $"{item.Code} {item.Name}: {asked.Asked.Words(item)} (was {wordsBefore})";
        audit.Add(new AuditEntry(AuditCategories.Admin, "inventory.item_price_changed",
            Fit($"{words}, asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName}", 1000))
        {
            EntityType = nameof(Item),
            EntityId = item.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { before, after = asked.Asked, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return words;
    }

    /// <summary>What the change lets through, for the person who decides.</summary>
    internal static string Consequences(Item item, ItemPricing from, ItemPricing to)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        List<string> effects = [];
        var medicine = PriceCeiling.AppliesTo(item.Type, from.CeilingPrice) || PriceCeiling.AppliesTo(item.Type, to.CeilingPrice);
        if (from.CeilingPrice is { } ceiling && to.CeilingPrice is null)
        {
            effects.Add(medicine
                ? "no NPPA ceiling is checked: packs are received and sold at whatever MRP is printed on them"
                : "the implant is charged at the MRP printed on its pack, without a ceiling");
        }
        else if (from.CeilingPrice is { } was && to.CeilingPrice is { } now && now > was)
        {
            effects.Add(medicine
                ? $"packs priced up to {PriceCeiling.Money(now)} a {item.BaseUnit} are received and sold (now {PriceCeiling.Money(was)})"
                : $"a patient is charged up to {PriceCeiling.Money(now)} a {item.BaseUnit} (now {PriceCeiling.Money(was)})");
        }

        if (from.GstRate != to.GstRate)
        {
            effects.Add($"every sale and purchase from now carries GST at {to.GstRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}% "
                        + $"instead of {from.GstRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%, on invoices, the day book and GST returns");
        }

        if (effects.Count == 0)
        {
            return "Its price control is loosened.";
        }

        var text = string.Join("; ", effects) + ".";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private async Task<Guid> FacilityAsync(CancellationToken ct) =>
        current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A ceiling price raised or removed, or a GST rate changed: whoever keeps the item master asks, another who approves prices decides.</summary>
internal sealed class ItemPriceApproval(ItemPriceApprovals prices) : IApprovalAction
{
    public string Kind => ItemPriceApprovals.Kind;

    public string Title => "Item ceiling price or GST rate";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => prices.ApplyAsync(request, ct);
}
