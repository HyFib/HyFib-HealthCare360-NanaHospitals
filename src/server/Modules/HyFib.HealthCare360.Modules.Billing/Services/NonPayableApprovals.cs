using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>What a non-payable rule says: for which payer (none: every payer), and which service or whole category.</summary>
public sealed record NonPayableRule(Guid? PayerId, Guid? ServiceItemId, ServiceCategory? Category, string? Notes)
{
    public static NonPayableRule Of(NonPayableItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new(item.PayerId, item.ServiceItemId, item.Category, item.Notes);
    }

    /// <summary>Whether two rules make the same charges non-payable, whatever their notes say.</summary>
    public bool SameRule(NonPayableRule other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return PayerId == other.PayerId && ServiceItemId == other.ServiceItemId && Category == other.Category;
    }

    /// <summary>
    /// The id the rule's item takes on the list, the same for the same rule whatever its notes: a second request to add
    /// it while the first waits is refused (one request per item waits at a time), rather than approved twice.
    /// </summary>
    public Guid ItemId() =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"non-payable|{PayerId:N}|{ServiceItemId:N}|{(int?)Category}"))).AsSpan(0, 16));

    /// <summary>
    /// Whether the rule makes a charge of this service, billed to this payer, non-payable: as a bill decides it, by
    /// <see cref="NonPayableItem.Matches"/>.
    /// </summary>
    public bool Reaches(Guid payerId, Guid serviceItemId, ServiceCategory category) =>
        new NonPayableItem { PayerId = PayerId, ServiceItemId = ServiceItemId, Category = Category }.Matches(payerId, serviceItemId, category);
}

/// <summary>A change of the non-payable list: a rule added, or a rule taken off.</summary>
public enum NonPayableChange
{
    Add = 0,
    Remove,
}

/// <summary>A change of the non-payable list asked for: the item it adds (with the id it will have) or removes, and its rule.</summary>
public sealed record NonPayablePayload(Guid ItemId, NonPayableChange Change, NonPayableRule Rule);

/// <summary>A change of the non-payable list waiting for a second person.</summary>
public sealed record PendingNonPayable(Guid ApprovalId, NonPayableChange Change, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// The non-payable list (IRDAI non-payables) decides who pays a charge on an insured bill: the payer, or the patient. It
/// changes only when two people agree (maker-checker): whoever keeps the payers asks to add a rule or take one off, and
/// someone else who holds <see cref="PlatformPermissions.PricesApprove"/> decides. Both ways move money: an item added
/// is charged to the patients, one taken off to the payer, who may disallow it at the claim. The approver is told what
/// the change does to the charges not yet billed of insured patients, on out-patient visits and stays in progress.
/// </summary>
public sealed class NonPayableApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "insurance.non_payable";

    public const string ExistsCode = "insurance.non_payable_exists";

    private const string NoReason = "No reason given.";

    /// <summary>Asks a second person to add <paramref name="rule"/> (as item <paramref name="itemId"/>) or to take it off.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(Guid itemId, NonPayableChange change, NonPayableRule rule, uint? version, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var (payer, item) = await NamesAsync(rule, ct);
        var impact = await ImpactAsync(rule, change, itemId, ct);
        List<ApprovalDetail> details =
        [
            new("Payer", payer),
            new("Item", item),
            change == NonPayableChange.Add
                ? new("Change", "Added to the non-payable list: on insured bills the patient pays for it, not the payer")
                : new("Change", "Taken off the non-payable list: on insured bills the payer is charged for it again, and an insurer may disallow it at the claim"),
            new("Not billed yet", impact.Charges == 0
                ? "No charge of an insured patient not yet billed would be charged otherwise"
                : $"{Count(impact.Charges)} charge(s) of {Count(impact.Patients)} insured patient(s), {Rupees(impact.Amount)}, not billed yet: their bills would charge them to {(change == NonPayableChange.Add ? "the patients instead of the payer" : "the payer instead of the patients")}"),
        ];
        if (!string.IsNullOrWhiteSpace(rule.Notes))
        {
            details.Add(new("Notes", rule.Notes));
        }

        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(NonPayableItem), itemId, version, await FacilityAsync(ct), null),
            new NonPayablePayload(itemId, change, rule),
            Fit($"{(change == NonPayableChange.Add ? "Non-payable for" : "No longer non-payable for")} {payer}: {item}", 300),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>The changes waiting for approval: the item each adds or removes, and its rule.</summary>
    public async Task<IReadOnlyList<(NonPayablePayload Asked, PendingNonPayable Pending)>> PendingAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var requests = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Payload, r.RequestedByName, r.RequestedAt })
            .ToListAsync(ct);
        return
        [
            .. requests.Select(r =>
            {
                var asked = ApprovalPayload.Read<NonPayablePayload>(r.Payload);
                return (asked, new PendingNonPayable(r.Id, asked.Change, r.RequestedByName, r.RequestedAt));
            }),
        ];
    }

    /// <summary>Makes an approved change, in the approval's transaction: adds the rule unless the list has it, or takes off the rule as it was.</summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<NonPayablePayload>(request.Payload);
        var (payer, item) = await NamesAsync(asked.Rule, ct);
        string words;
        if (asked.Change == NonPayableChange.Add)
        {
            var rules = await db.Set<NonPayableItem>().Where(n => n.PayerId == asked.Rule.PayerId).ToListAsync(ct);
            if (rules.Any(n => NonPayableRule.Of(n).SameRule(asked.Rule)))
            {
                return Error.Rule(ExistsCode, "The non-payable list already has this item for this payer, so nothing was added.");
            }

            db.Add(new NonPayableItem
            {
                Id = asked.ItemId,
                PayerId = asked.Rule.PayerId,
                ServiceItemId = asked.Rule.ServiceItemId,
                Category = asked.Rule.ServiceItemId is null ? asked.Rule.Category : null,
                Notes = asked.Rule.Notes,
            });
            words = $"non-payable for {payer}: {item}";
        }
        else
        {
            var existing = await db.Set<NonPayableItem>().FirstOrDefaultAsync(n => n.Id == asked.ItemId, ct);
            if (existing is null || !NonPayableRule.Of(existing).SameRule(asked.Rule))
            {
                return Error.Rule(TariffApprovals.ChangedCode, "The non-payable item was removed or changed after this was asked for, so nothing was taken off.");
            }

            db.Remove(existing);
            words = $"no longer non-payable for {payer}: {item}";
        }

        audit.Add(new AuditEntry(AuditCategories.Admin, "insurance.non_payable_changed",
            Fit($"{words} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(NonPayableItem),
            EntityId = asked.ItemId.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { change = asked.Change.ToString(), rule = asked.Rule, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return words;
    }

    /// <summary>
    /// The charges not yet billed of insured patients (out-patient visits on a payer, and stays in progress on one) that
    /// the change would charge otherwise: those the rule reaches that no other rule of the list already makes non-payable.
    /// </summary>
    private async Task<(int Charges, int Patients, decimal Amount)> ImpactAsync(NonPayableRule rule, NonPayableChange change, Guid itemId, CancellationToken ct)
    {
        var onVisits = await (
                from c in db.Set<Charge>().AsNoTracking()
                join v in db.Set<Visit>().AsNoTracking() on c.VisitId equals (Guid?)v.Id
                where c.Status == ChargeStatus.Unbilled && c.AdmissionId == null && v.PayerType != PayerType.Self && v.PayerId != null
                    && (rule.PayerId == null || v.PayerId == rule.PayerId)
                select new { c.Id, c.PatientId, PayerId = v.PayerId!.Value, c.ServiceItemId, c.Category, c.NetAmount })
            .ToListAsync(ct);
        var onStays = await (
                from c in db.Set<Charge>().AsNoTracking()
                join a in db.Set<IpAccount>().AsNoTracking() on c.AdmissionId equals (Guid?)a.AdmissionId
                where c.Status == ChargeStatus.Unbilled && (a.Status == IpAccountStatus.Open || a.Status == IpAccountStatus.DischargeInitiated)
                    && a.PayerId != null && (rule.PayerId == null || a.PayerId == rule.PayerId)
                select new { c.Id, c.PatientId, PayerId = a.PayerId!.Value, c.ServiceItemId, c.Category, c.NetAmount })
            .ToListAsync(ct);
        var others = await db.Set<NonPayableItem>().AsNoTracking().Where(n => n.Id != itemId).ToListAsync(ct);
        var moved = onVisits.Concat(onStays)
            .DistinctBy(c => c.Id)
            .Where(c => rule.Reaches(c.PayerId, c.ServiceItemId, c.Category) && !others.Any(o => o.Matches(c.PayerId, c.ServiceItemId, c.Category)))
            .ToList();
        return (moved.Count, moved.Select(c => c.PatientId).Distinct().Count(), moved.Sum(c => c.NetAmount));
    }

    private async Task<(string Payer, string Item)> NamesAsync(NonPayableRule rule, CancellationToken ct)
    {
        var payer = rule.PayerId is { } payerId
            ? await db.Set<Payer>().AsNoTracking().Where(p => p.Id == payerId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "a payer"
            : "every payer";
        var item = rule.ServiceItemId is { } serviceId
            ? await db.Set<ServiceItem>().AsNoTracking().Where(s => s.Id == serviceId).Select(s => s.Code + " " + s.Name).FirstOrDefaultAsync(ct) ?? "a service"
            : $"every {Words(rule.Category?.ToString() ?? "other")} service";
        return (payer, item);
    }

    private async Task<Guid> FacilityAsync(CancellationToken ct) =>
        current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);

    /// <summary>An enum value in words: "BedCharge" → "bed charge".</summary>
    private static string Words(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    private static string Rupees(decimal amount) => "₹" + Money.FormatInr(amount);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>The non-payable list changed: whoever keeps the payers asks, another who may approve prices decides (maker-checker).</summary>
internal sealed class NonPayableApproval(NonPayableApprovals nonPayables) : IApprovalAction
{
    public string Kind => NonPayableApprovals.Kind;

    public string Title => "Non-payable item";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => nonPayables.ApplyAsync(request, ct);
}
