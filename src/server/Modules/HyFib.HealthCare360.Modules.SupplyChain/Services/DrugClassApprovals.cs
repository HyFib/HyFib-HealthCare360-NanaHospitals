using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>A drug's classification asked for: the item, the class it replaces, and the class asked for.</summary>
public sealed record DrugClassPayload(Guid ItemId, DrugSafetyClass Replaced, DrugSafetyClass Asked);

/// <summary>A drug's classification waiting for a second person.</summary>
public sealed record PendingDrugClass(Guid ApprovalId, DrugSafetyClass Asked, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// A drug handled less strictly (no longer controlled, no longer high-alert, or on a lower schedule) is changed only when
/// two people agree (maker-checker): whoever keeps the item master asks, and someone else who holds
/// <see cref="SupplyChainPermissions.DrugClassApprove"/> (a pharmacist) decides. The rest of the item is saved at once, and
/// the drug keeps its class until then. Made stricter, a class applies at once (<see cref="DrugSafetyClass"/>). The class
/// is copied onto the ward's medicine orders when they are made, so the change reaches orders made after it.
/// </summary>
public sealed class DrugClassApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "inventory.drug_class";

    /// <summary>A save asked for another class while one waits for approval.</summary>
    public const string WaitingCode = "inventory.drug_class_waiting";

    private const string NoReason = "No reason given.";

    /// <summary>Asks a second person to set <paramref name="item"/>'s class to <paramref name="asked"/>, in the caller's transaction.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(Item item, DrugSafetyClass replaced, DrugSafetyClass asked, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(asked);
        List<ApprovalDetail> details =
        [
            new("Item", $"{item.Code} {item.Name}"),
            new("Now", replaced.Words()),
            new("Asked for", asked.Words()),
            new("What it changes", Consequences(replaced, asked)),
        ];
        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Item), item.Id, item.Version, await FacilityAsync(ct), null),
            new DrugClassPayload(item.Id, replaced, asked),
            Fit($"{item.Code} {item.Name}: {asked.Words()} (now {replaced.Words()})", 300),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>The class waiting for approval for this item, if any.</summary>
    public async Task<PendingDrugClass?> PendingAsync(Guid itemId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Item) && r.SubjectId == itemId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Payload, r.RequestedByName, r.RequestedAt })
            .FirstOrDefaultAsync(ct);
        return request is null
            ? null
            : new PendingDrugClass(request.Id, ApprovalPayload.Read<DrugClassPayload>(request.Payload).Asked, request.RequestedByName, request.RequestedAt);
    }

    /// <summary>Sets an approved class, in the approval's transaction, if the item still has the class the request replaces.</summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DrugClassPayload>(request.Payload);
        var item = await db.Set<Item>().FirstOrDefaultAsync(i => i.Id == asked.ItemId, ct);
        if (item is null)
        {
            return Error.NotFound(nameof(Item), asked.ItemId);
        }

        // Only the class is compared: a later change of the item's reorder level does not stand in the way, a change of
        // its class (stricter, made at once since) does.
        var before = DrugSafetyClass.Of(item);
        if (before != asked.Replaced)
        {
            return Error.Rule("approval.stale",
                $"{item.Code} {item.Name}'s classification changed after this was asked for, so nothing was changed. Ask again, for the item as it is now.");
        }

        asked.Asked.ApplyTo(item);
        var valid = item.Validate();
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        var words = $"{item.Code} {item.Name}: {asked.Asked.Words()} (was {before.Words()})";
        audit.Add(new AuditEntry(AuditCategories.Admin, "inventory.drug_class_changed",
            Fit($"{words}, asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName}", 1000))
        {
            EntityType = nameof(Item),
            EntityId = item.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { before, after = asked.Asked, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return words;
    }

    /// <summary>What handling the drug less strictly switches off, for the person who decides.</summary>
    internal static string Consequences(DrugSafetyClass from, DrugSafetyClass to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var registered = (Before: Item.NeedsRegister(from.Schedule, from.IsControlled), After: Item.NeedsRegister(to.Schedule, to.IsControlled));
        List<string> effects = [];
        if (registered.Before && !registered.After)
        {
            effects.Add("its sales and issues are no longer entered in the statutory register (H1, X or NDPS)");
        }

        if ((registered.Before || from.IsHighAlert) && !(registered.After || to.IsHighAlert))
        {
            effects.Add("a dose given on the ward is no longer witnessed by a second nurse, for orders made from now on");
        }

        if (Item.NeedsPrescription(from.Schedule, from.IsControlled) && !Item.NeedsPrescription(to.Schedule, to.IsControlled))
        {
            effects.Add("it is sold without a prescription");
        }

        if (effects.Count == 0)
        {
            return "It is handled less strictly.";
        }

        var text = string.Join("; ", effects) + ".";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private async Task<Guid> FacilityAsync(CancellationToken ct) =>
        current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A drug handled less strictly: whoever keeps the item master asks, another who may approve it (a pharmacist) decides.</summary>
internal sealed class DrugClassApproval(DrugClassApprovals classes) : IApprovalAction
{
    public string Kind => DrugClassApprovals.Kind;

    public string Title => "Drug classification";

    public string ApprovePermission => SupplyChainPermissions.DrugClassApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => classes.ApplyAsync(request, ct);
}
