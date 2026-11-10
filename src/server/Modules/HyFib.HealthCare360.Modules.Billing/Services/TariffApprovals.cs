using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// What a tariff plan decides about what its patients pay, and which patients it prices (maker-checker): its type and
/// facility (which patients a default plan prices), its discount on every discountable service, its bed-class
/// multipliers, whether it is the default of its type, and whether it is in use at all. A payer's patients move to another
/// plan's prices when theirs is taken out of use, so for a plan nothing only narrows: every change waits.
/// </summary>
public sealed record TariffPlanTerms(
    TariffType Type, Guid? FacilityId, decimal DefaultDiscountPercent, bool IsDefault, bool IsActive, IReadOnlyList<BedMultiplierInput> BedMultipliers)
{
    public static TariffPlanTerms Of(TariffPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Create(plan.Type, plan.FacilityId, plan.DefaultDiscountPercent, plan.IsDefault, plan.IsActive,
            plan.BedMultipliers.Select(m => new BedMultiplierInput(m.BedServiceItemId, m.Multiplier)));
    }

    /// <summary>The terms with the multipliers in a fixed order, and a default only while in use (as the plan keeps it).</summary>
    public static TariffPlanTerms Create(
        TariffType type, Guid? facilityId, decimal discountPercent, bool isDefault, bool isActive, IEnumerable<BedMultiplierInput>? multipliers) =>
        new(type, facilityId, discountPercent, isDefault && isActive, isActive, [.. (multipliers ?? []).OrderBy(m => m.BedServiceItemId)]);

    /// <summary>
    /// A new plan's terms until a second person approves the ones asked: out of use, not a default, no discount and no
    /// multipliers. Its type and facility are kept, as they price nobody while it is out of use.
    /// </summary>
    public TariffPlanTerms OutOfUse() => this with { DefaultDiscountPercent = 0m, IsDefault = false, IsActive = false, BedMultipliers = [] };

    public bool Equals(TariffPlanTerms? other) =>
        other is not null && Type == other.Type && FacilityId == other.FacilityId && DefaultDiscountPercent == other.DefaultDiscountPercent
        && IsDefault == other.IsDefault && IsActive == other.IsActive && BedMultipliers.SequenceEqual(other.BedMultipliers);

    public override int GetHashCode() => HashCode.Combine(Type, FacilityId, DefaultDiscountPercent, IsDefault, IsActive, BedMultipliers.Count);

    public void ApplyTo(TariffPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Type = Type;
        plan.FacilityId = FacilityId;
        plan.DefaultDiscountPercent = DefaultDiscountPercent;
        plan.IsActive = IsActive;
        plan.IsDefault = IsDefault && IsActive;
        plan.BedMultipliers.Clear();
        foreach (var multiplier in BedMultipliers)
        {
            plan.BedMultipliers.Add(new TariffBedMultiplier { TariffPlanId = plan.Id, BedServiceItemId = multiplier.BedServiceItemId, Multiplier = multiplier.Multiplier });
        }
    }
}

/// <summary>A plan's terms asked for, and the terms they replace (a change made meanwhile is not overwritten).</summary>
public sealed record TariffPlanPayload(Guid PlanId, TariffPlanTerms Asked, TariffPlanTerms Replaced);

/// <summary>A price row of a plan as it stood when a change was asked for.</summary>
public sealed record TariffPriceRow(Guid Id, Guid ServiceItemId, decimal Price, DateOnly EffectiveFrom, DateOnly? EffectiveTo, uint Version);

/// <summary>
/// Prices of a plan asked for: rows to set (a new start date adds a revision; the same start date changes it), rows to
/// remove, and every row of the services touched as it stood (<see cref="Replaced"/>), so a change made meanwhile to any of
/// them is never overwritten.
/// </summary>
public sealed record TariffPricesPayload(Guid PlanId, IReadOnlyList<SaveTariffPriceRequest> Set, IReadOnlyList<Guid> Remove, IReadOnlyList<TariffPriceRow> Replaced);

/// <summary>A plan's terms waiting for a second person.</summary>
public sealed record PendingTariffTerms(Guid ApprovalId, TariffPlanTerms Asked, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>A plan's prices waiting for a second person: the rows to set and those to remove.</summary>
public sealed record PendingTariffPrices(
    Guid ApprovalId, IReadOnlyList<SaveTariffPriceRequest> Set, IReadOnlyList<Guid> Remove, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// A tariff plan's prices and terms change only when two people agree (maker-checker): every change to a plan's prices
/// (one, a bulk revision, or one removed) and to its terms is asked for, and someone else who holds
/// <see cref="PlatformPermissions.PricesApprove"/> makes it. Approving makes the change only over the rows and terms it
/// replaces. A new plan is made out of use and goes live with the terms approved.
/// </summary>
public sealed class TariffApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string TermsKind = "billing.tariff_plan";
    public const string PricesKind = "billing.tariff_prices";

    /// <summary>Refused at approval: the plan's terms or the prices it would change were changed after it was asked for.</summary>
    public const string ChangedCode = "prices.changed";

    private const string NoReason = "No reason given.";

    /// <summary>Asks a second person to set <paramref name="plan"/>'s terms to <paramref name="asked"/>.</summary>
    public async Task<Result<ApprovalRequestDto>> AskTermsAsync(
        TariffPlan plan, TariffPlanTerms replaced, TariffPlanTerms asked, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(asked);
        var beds = await BedNamesAsync([.. replaced.BedMultipliers.Concat(asked.BedMultipliers).Select(m => m.BedServiceItemId)], ct);
        List<ApprovalDetail> details = [new("Plan", $"{plan.Code} {plan.Name}"), .. Changes(replaced, asked, beds).Select(c => new ApprovalDetail(c.Label, c.Value))];
        return await approvals.RequestAsync(
            TermsKind,
            new ApprovalSubject(nameof(TariffPlan), plan.Id, plan.Version, await FacilityAsync(plan, ct), null),
            new TariffPlanPayload(plan.Id, asked, replaced),
            Fit($"Tariff plan {plan.Code} {plan.Name}: {string.Join("; ", Changes(replaced, asked, beds).Select(c => c.Words))}", 500),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>
    /// Asks a second person to set and remove these prices of <paramref name="plan"/>, recording every row of the services
    /// touched as it stands. A row with a start date before today is flagged: charges already posted keep their price.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> AskPricesAsync(
        TariffPlan plan, IReadOnlyList<SaveTariffPriceRequest> set, IReadOnlyList<Guid> remove, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(remove);
        var replaced = await RowsTouchedAsync(plan.Id, set, remove, ct);
        var serviceIds = set.Select(r => r.ServiceItemId).Concat(replaced.Select(r => r.ServiceItemId)).Distinct().ToList();
        var services = await db.Set<ServiceItem>().AsNoTracking().Where(s => serviceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => $"{s.Code} {s.Name}", ct);
        var today = BillingTime.Today(time);

        List<ApprovalDetail> details = [new("Plan", $"{plan.Code} {plan.Name}")];
        foreach (var row in set.Take(20))
        {
            var before = replaced.Where(r => r.ServiceItemId == row.ServiceItemId && r.IsEffectiveOn(row.EffectiveFrom)).Select(r => r.Price).FirstOrDefault();
            var was = replaced.Any(r => r.ServiceItemId == row.ServiceItemId && r.IsEffectiveOn(row.EffectiveFrom)) ? $"{Rupees(before)} → " : string.Empty;
            details.Add(new(services.GetValueOrDefault(row.ServiceItemId, "Service"),
                $"{was}{Rupees(row.Price)} from {Day(row.EffectiveFrom)}{(row.EffectiveTo is { } to ? $" to {Day(to)}" : string.Empty)}"));
        }

        if (set.Count > 20)
        {
            details.Add(new("And", $"{set.Count - 20} more prices"));
        }

        if (set.Count > 1)
        {
            // A revision in one line: how many go up, down or are new, and the largest change either way.
            var changes = set.Select(row => (Row: row, Before: replaced.FirstOrDefault(r => r.ServiceItemId == row.ServiceItemId && r.IsEffectiveOn(row.EffectiveFrom))))
                .ToList();
            var compared = changes.Where(c => c.Before is { Price: > 0m }).Select(c => (c.Row.Price - c.Before!.Price) / c.Before.Price * 100m).ToList();
            details.Add(new("Revision",
                $"{compared.Count(p => p > 0)} dearer, {compared.Count(p => p < 0)} cheaper, {changes.Count(c => c.Before is null)} new"
                + (compared.Count > 0 ? $"; largest change {Signed(compared.MaxBy(Math.Abs))}" : string.Empty)));
        }

        foreach (var row in replaced.Where(r => remove.Contains(r.Id)))
        {
            details.Add(new(services.GetValueOrDefault(row.ServiceItemId, "Service"),
                $"{Rupees(row.Price)} from {Day(row.EffectiveFrom)} removed: the service is charged at its own price on those dates"));
        }

        var backdated = set.Where(r => r.EffectiveFrom < today).Select(r => r.EffectiveFrom).ToList();
        if (backdated.Count > 0)
        {
            details.Add(new("Backdated", $"From {Day(backdated.Min())}, before today: charges already posted keep their price"));
        }

        var what = (set.Count, remove.Count) switch
        {
            (1, 0) => $"{services.GetValueOrDefault(set[0].ServiceItemId, "a service")} at {Rupees(set[0].Price)} from {Day(set[0].EffectiveFrom)}",
            (0, 1) => $"price of {services.GetValueOrDefault(replaced.First(r => r.Id == remove[0]).ServiceItemId, "a service")} removed",
            _ => $"{set.Count} price(s) set{(remove.Count > 0 ? $", {remove.Count} removed" : string.Empty)}",
        };
        return await approvals.RequestAsync(
            PricesKind,
            new ApprovalSubject(nameof(TariffPlan), plan.Id, plan.Version, await FacilityAsync(plan, ct), null),
            new TariffPricesPayload(plan.Id, set, remove, replaced),
            Fit($"Prices of tariff plan {plan.Code} {plan.Name}: {what}", 500),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>The terms waiting for approval for this plan, if any.</summary>
    public async Task<PendingTariffTerms?> PendingTermsAsync(Guid planId, CancellationToken ct) =>
        await WaitingAsync(TermsKind, planId, ct) is { } request
            ? new PendingTariffTerms(request.Id, ApprovalPayload.Read<TariffPlanPayload>(request.Payload).Asked, request.RequestedByName, request.RequestedAt)
            : null;

    /// <summary>The prices waiting for approval for this plan, if any.</summary>
    public async Task<PendingTariffPrices?> PendingPricesAsync(Guid planId, CancellationToken ct)
    {
        if (await WaitingAsync(PricesKind, planId, ct) is not { } request)
        {
            return null;
        }

        var asked = ApprovalPayload.Read<TariffPricesPayload>(request.Payload);
        return new PendingTariffPrices(request.Id, asked.Set, asked.Remove, request.RequestedByName, request.RequestedAt);
    }

    /// <summary>Sets an approved plan's terms, in the approval's transaction, if they are still the ones the request replaces.</summary>
    internal async Task<Result<string>> ApplyTermsAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<TariffPlanPayload>(request.Payload);
        var plan = await db.Set<TariffPlan>().Include(p => p.BedMultipliers).FirstOrDefaultAsync(p => p.Id == asked.PlanId, ct);
        if (plan is null)
        {
            return Error.Rule("prices.plan_gone", "The tariff plan no longer exists, so nothing was set.");
        }

        var before = TariffPlanTerms.Of(plan);
        if (before != asked.Replaced)
        {
            return Error.Rule(ChangedCode, $"Tariff plan {plan.Code}'s terms changed after this was asked for, so nothing was set. Ask again.");
        }

        asked.Asked.ApplyTo(plan);
        if (plan.IsDefault)
        {
            // One default per plan type and facility scope: the approved default replaces the previous one.
            foreach (var other in await db.Set<TariffPlan>()
                         .Where(p => p.Id != plan.Id && p.IsDefault && p.Type == plan.Type && p.FacilityId == plan.FacilityId)
                         .ToListAsync(ct))
            {
                other.IsDefault = false;
            }
        }

        var beds = await BedNamesAsync([.. before.BedMultipliers.Concat(asked.Asked.BedMultipliers).Select(m => m.BedServiceItemId)], ct);
        var words = string.Join("; ", Changes(before, asked.Asked, beds).Select(c => c.Words));
        audit.Add(new AuditEntry(AuditCategories.Admin, "tariff.plan_changed",
            Fit($"{plan.Code} {plan.Name}: {words} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(TariffPlan),
            EntityId = plan.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { before, after = asked.Asked, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return words;
    }

    /// <summary>
    /// Sets and removes an approved plan's prices, in the approval's transaction, if every row of the services touched is
    /// still as it was when they were asked for.
    /// </summary>
    internal async Task<Result<string>> ApplyPricesAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<TariffPricesPayload>(request.Payload);
        var plan = await db.Set<TariffPlan>().FirstOrDefaultAsync(p => p.Id == asked.PlanId, ct);
        if (plan is null)
        {
            return Error.Rule("prices.plan_gone", "The tariff plan no longer exists, so nothing was set.");
        }

        var now = await RowsTouchedAsync(plan.Id, asked.Set, asked.Remove, ct);
        if (!Same(now, asked.Replaced))
        {
            return Error.Rule(ChangedCode, $"Prices of tariff plan {plan.Code} changed after this was asked for, so nothing was set. Ask again.");
        }

        var rows = await db.Set<TariffPrice>().Where(p => p.TariffPlanId == plan.Id && asked.Remove.Contains(p.Id)).ToListAsync(ct);
        db.RemoveRange(rows);
        var outcome = await UpsertAsync(db, plan.Id, asked.Set, ct);
        audit.Add(new AuditEntry(AuditCategories.Admin, "tariff.prices_changed",
            Fit($"{plan.Code} {plan.Name}: {outcome.Created} price(s) added, {outcome.Updated} changed, {rows.Count} removed (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(TariffPlan),
            EntityId = plan.Id.ToString(),
            Severity = AuditSeverity.Warning,
            // Each price row's own audit event holds it as it was and as it is; this one says what was approved, and by whom.
            Details = new { added = outcome.Created, changed = outcome.Updated, removed = rows.Count, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return $"{outcome.Created} price(s) added, {outcome.Updated} changed, {rows.Count} removed";
    }

    /// <summary>
    /// Adds or updates plan prices. A new price closes the previous open-ended price of the same service the day before it
    /// starts, so revisions never overlap and history is kept. The caller saves.
    /// </summary>
    internal static async Task<BulkTariffPricesResult> UpsertAsync(HospitalDbContext db, Guid planId, IReadOnlyList<SaveTariffPriceRequest> requests, CancellationToken ct)
    {
        var itemIds = requests.Select(r => r.ServiceItemId).Distinct().ToList();
        var existing = await db.Set<TariffPrice>().Where(p => p.TariffPlanId == planId && itemIds.Contains(p.ServiceItemId)).ToListAsync(ct);
        int created = 0, updated = 0;
        foreach (var request in requests)
        {
            if (existing.FirstOrDefault(p => p.ServiceItemId == request.ServiceItemId && p.EffectiveFrom == request.EffectiveFrom) is { } same)
            {
                same.Price = request.Price;
                same.EffectiveTo = request.EffectiveTo;
                updated++;
                continue;
            }

            foreach (var previous in existing.Where(p => p.ServiceItemId == request.ServiceItemId && p.EffectiveFrom < request.EffectiveFrom
                                                         && (p.EffectiveTo is null || p.EffectiveTo >= request.EffectiveFrom)))
            {
                previous.EffectiveTo = request.EffectiveFrom.AddDays(-1);
            }

            var price = new TariffPrice
            {
                TariffPlanId = planId,
                ServiceItemId = request.ServiceItemId,
                Price = request.Price,
                EffectiveFrom = request.EffectiveFrom,
                EffectiveTo = request.EffectiveTo,
            };
            db.Add(price);
            existing.Add(price);
            created++;
        }

        return new BulkTariffPricesResult(created, updated);
    }

    /// <summary>Whether two sets of rows are the same rows, in any order.</summary>
    internal static bool Same(IReadOnlyCollection<TariffPriceRow> now, IReadOnlyCollection<TariffPriceRow> then) =>
        now.Count == then.Count && now.OrderBy(r => r.Id).SequenceEqual(then.OrderBy(r => r.Id));

    /// <summary>What changes from <paramref name="from"/> to <paramref name="to"/>: a detail row and its words, each.</summary>
    internal static IEnumerable<(string Label, string Value, string Words)> Changes(
        TariffPlanTerms from, TariffPlanTerms to, IReadOnlyDictionary<Guid, string> beds)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(beds);
        if (from.IsActive != to.IsActive)
        {
            yield return to.IsActive
                ? ("In use", "Put in use", "put in use")
                : ("In use", "Taken out of use: its patients are charged by their type's default plan", "taken out of use");
        }

        if (from.IsDefault != to.IsDefault)
        {
            yield return to.IsDefault
                ? ("Default", $"Becomes the default {to.Type} plan", $"the default {to.Type} plan")
                : ("Default", "No longer the default plan", "no longer the default");
        }

        if (from.Type != to.Type)
        {
            yield return ("Type", $"{from.Type} → {to.Type}", $"type {from.Type} → {to.Type}");
        }

        if (from.FacilityId != to.FacilityId)
        {
            yield return ("Facility", to.FacilityId is null ? "Every facility" : "One facility", to.FacilityId is null ? "for every facility" : "for one facility");
        }

        if (from.DefaultDiscountPercent != to.DefaultDiscountPercent)
        {
            yield return ("Discount", $"{Percent(from.DefaultDiscountPercent)} → {Percent(to.DefaultDiscountPercent)}",
                $"discount {Percent(from.DefaultDiscountPercent)} → {Percent(to.DefaultDiscountPercent)}");
        }

        foreach (var bed in from.BedMultipliers.Select(m => m.BedServiceItemId).Union(to.BedMultipliers.Select(m => m.BedServiceItemId)))
        {
            var was = from.BedMultipliers.FirstOrDefault(m => m.BedServiceItemId == bed)?.Multiplier;
            var now = to.BedMultipliers.FirstOrDefault(m => m.BedServiceItemId == bed)?.Multiplier;
            if (was != now)
            {
                var name = beds.GetValueOrDefault(bed, "A bed class");
                var value = $"{Times(was)} → {Times(now)}";
                yield return ($"{name} multiplier", value, $"{name} {value}");
            }
        }
    }

    /// <summary>Every row of the services in <paramref name="set"/>, and the rows in <paramref name="remove"/>, as they stand.</summary>
    private async Task<IReadOnlyList<TariffPriceRow>> RowsTouchedAsync(
        Guid planId, IReadOnlyList<SaveTariffPriceRequest> set, IReadOnlyList<Guid> remove, CancellationToken ct)
    {
        var serviceIds = set.Select(r => r.ServiceItemId).Distinct().ToList();
        return await db.Set<TariffPrice>().AsNoTracking()
            .Where(p => p.TariffPlanId == planId && (serviceIds.Contains(p.ServiceItemId) || remove.Contains(p.Id)))
            .Select(p => new TariffPriceRow(p.Id, p.ServiceItemId, p.Price, p.EffectiveFrom, p.EffectiveTo, p.Version))
            .ToListAsync(ct);
    }

    private async Task<ApprovalRequest?> WaitingAsync(string kind, Guid planId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == kind && r.SubjectType == nameof(TariffPlan) && r.SubjectId == planId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<Guid> FacilityAsync(TariffPlan plan, CancellationToken ct) =>
        plan.FacilityId ?? current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);

    private async Task<IReadOnlyDictionary<Guid, string>> BedNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<ServiceItem>().AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

    private static string Rupees(decimal amount) => "₹" + Money.FormatInr(amount);

    private static string Day(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Signed(decimal percent) => (percent > 0 ? "+" : string.Empty) + percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Times(decimal? multiplier) => multiplier is { } m ? "×" + m.ToString("0.####", CultureInfo.InvariantCulture) : "none";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

internal static class TariffPriceRowExtensions
{
    public static bool IsEffectiveOn(this TariffPriceRow row, DateOnly date) => row.EffectiveFrom <= date && (row.EffectiveTo is null || date <= row.EffectiveTo.Value);
}

/// <summary>A tariff plan's terms changed: one person asks, another who may approve prices decides (maker-checker).</summary>
internal sealed class TariffPlanApproval(TariffApprovals tariffs) : IApprovalAction
{
    public string Kind => TariffApprovals.TermsKind;

    public string Title => "Tariff plan";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => tariffs.ApplyTermsAsync(request, ct);
}

/// <summary>A tariff plan's prices changed: one person asks, another who may approve prices decides (maker-checker).</summary>
internal sealed class TariffPricesApproval(TariffApprovals tariffs) : IApprovalAction
{
    public string Kind => TariffApprovals.PricesKind;

    public string Title => "Tariff plan prices";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => tariffs.ApplyPricesAsync(request, ct);
}
