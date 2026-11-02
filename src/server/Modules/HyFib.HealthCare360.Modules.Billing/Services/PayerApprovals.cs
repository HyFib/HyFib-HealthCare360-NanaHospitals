using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// What a payer's agreement decides about what its patients pay (maker-checker): its category (whose default plan prices
/// its patients while it has no plan of its own), its own tariff plan, the co-pay its patients pay where their policy
/// states none, and whether it is in use. Taking a payer out of use only stops new visits and stays being billed to it
/// (those open keep it), so that narrows and is made at once; every other change waits for a second person.
/// </summary>
public sealed record PayerTerms(PayerCategory Category, Guid? TariffPlanId, decimal DefaultCoPayPercent, bool IsActive)
{
    public static PayerTerms Of(Payer payer)
    {
        ArgumentNullException.ThrowIfNull(payer);
        return new(payer.Category, payer.TariffPlanId, payer.DefaultCoPayPercent, payer.IsActive);
    }

    /// <summary>
    /// A new payer's terms until a second person approves the ones asked: out of use, with no plan of its own and no
    /// co-pay. Its category is kept, as it prices nobody while it is out of use.
    /// </summary>
    public PayerTerms OutOfUse() => this with { TariffPlanId = null, DefaultCoPayPercent = 0m, IsActive = false };

    /// <summary>What a save makes at once over these terms: only taking the payer out of use.</summary>
    public PayerTerms Narrowed(PayerTerms asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        return this with { IsActive = IsActive && asked.IsActive };
    }

    public void ApplyTo(Payer payer)
    {
        ArgumentNullException.ThrowIfNull(payer);
        payer.Category = Category;
        payer.TariffPlanId = TariffPlanId;
        payer.DefaultCoPayPercent = DefaultCoPayPercent;
        payer.IsActive = IsActive;
    }
}

/// <summary>A payer's terms asked for, and the terms they replace (a change made meanwhile is not overwritten).</summary>
public sealed record PayerTermsPayload(Guid PayerId, PayerTerms Asked, PayerTerms Replaced);

/// <summary>A payer's terms waiting for a second person.</summary>
public sealed record PendingPayerTerms(Guid ApprovalId, PayerTerms Asked, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// A payer's terms change only when two people agree (maker-checker): the insurance desk asks, and someone else who holds
/// <see cref="PlatformPermissions.PricesApprove"/> makes the change, only over the terms it replaces. A new payer is made
/// out of use and goes live with the terms approved.
/// </summary>
public sealed class PayerApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "insurance.payer_terms";

    private const string NoReason = "No reason given.";

    /// <summary>Asks a second person to set <paramref name="payer"/>'s terms to <paramref name="asked"/>.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(Payer payer, PayerTerms replaced, PayerTerms asked, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payer);
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(asked);
        var plans = await PlansAsync([replaced.TariffPlanId, asked.TariffPlanId], ct);
        var changes = Changes(replaced, asked, plans).ToList();
        List<ApprovalDetail> details = [new("Payer", $"{payer.Code} {payer.Name}"), .. changes.Select(c => new ApprovalDetail(c.Label, c.Value))];
        if (replaced.Category != asked.Category || replaced.TariffPlanId != asked.TariffPlanId)
        {
            // A stay keeps the plan it was admitted on; an out-patient's services are priced when they are charged.
            details.Add(new("Applies to", "Out-patient services charged after approval, and admissions after it. Stays already open keep their plan."));
        }

        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Payer), payer.Id, payer.Version, await FacilityAsync(ct), null),
            new PayerTermsPayload(payer.Id, asked, replaced),
            Fit($"Payer {payer.Code} {payer.Name}: {string.Join("; ", changes.Select(c => c.Words))}", 500),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>The terms waiting for approval for this payer, if any.</summary>
    public async Task<PendingPayerTerms?> PendingAsync(Guid payerId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Payer) && r.SubjectId == payerId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
        return request is null
            ? null
            : new PendingPayerTerms(request.Id, ApprovalPayload.Read<PayerTermsPayload>(request.Payload).Asked, request.RequestedByName, request.RequestedAt);
    }

    /// <summary>Sets an approved payer's terms, in the approval's transaction, if they are still the ones the request replaces.</summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PayerTermsPayload>(request.Payload);
        var payer = await db.Set<Payer>().FirstOrDefaultAsync(p => p.Id == asked.PayerId, ct);
        if (payer is null)
        {
            return Error.Rule("prices.payer_gone", "The payer no longer exists, so nothing was set.");
        }

        var before = PayerTerms.Of(payer);
        if (before != asked.Replaced)
        {
            return Error.Rule(TariffApprovals.ChangedCode, $"Payer {payer.Code}'s terms changed after this was asked for, so nothing was set. Ask again.");
        }

        asked.Asked.ApplyTo(payer);
        var plans = await PlansAsync([before.TariffPlanId, asked.Asked.TariffPlanId], ct);
        var words = string.Join("; ", Changes(before, asked.Asked, plans).Select(c => c.Words));
        audit.Add(new AuditEntry(AuditCategories.Admin, "payer.terms_changed",
            Fit($"{payer.Code} {payer.Name}: {words} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(Payer),
            EntityId = payer.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { before, after = asked.Asked, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return words;
    }

    /// <summary>What changes from <paramref name="from"/> to <paramref name="to"/>: a detail row and its words, each.</summary>
    internal static IEnumerable<(string Label, string Value, string Words)> Changes(PayerTerms from, PayerTerms to, IReadOnlyDictionary<Guid, PlanName> plans)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(plans);
        if (from.IsActive != to.IsActive)
        {
            yield return to.IsActive
                ? ("In use", "Put in use: visits and stays can be billed to it", "put in use")
                : ("In use", "Taken out of use: no new visit or stay is billed to it", "taken out of use");
        }

        if (from.Category != to.Category)
        {
            var value = $"{Category(from.Category)} → {Category(to.Category)}";
            yield return ("Category", value, $"category {value}");
        }

        if (from.TariffPlanId != to.TariffPlanId)
        {
            yield return ("Tariff plan", $"{Plan(from.TariffPlanId, plans)} → {Plan(to.TariffPlanId, plans)}",
                $"plan {Plan(from.TariffPlanId, plans, brief: true)} → {Plan(to.TariffPlanId, plans, brief: true)}");
        }

        if (from.DefaultCoPayPercent != to.DefaultCoPayPercent)
        {
            var value = $"{Percent(from.DefaultCoPayPercent)} → {Percent(to.DefaultCoPayPercent)}";
            yield return ("Co-pay", $"{value} of each bill, where the patient's policy states none", $"co-pay {value}");
        }
    }

    /// <summary>A plan's name, and whether it is in use (a plan out of use prices nobody: its type's default does).</summary>
    internal sealed record PlanName(string Name, bool IsActive);

    private static string Plan(Guid? id, IReadOnlyDictionary<Guid, PlanName> plans, bool brief = false)
    {
        if (id is not { } planId)
        {
            return brief ? "none" : "none of its own: its type's default plan";
        }

        var plan = plans.GetValueOrDefault(planId, new PlanName("A tariff plan", true));
        return brief || plan.IsActive ? plan.Name : $"{plan.Name} (out of use: its type's default plan prices its patients until it is put in use)";
    }

    private static string Category(PayerCategory category) => category switch
    {
        PayerCategory.Tpa => "TPA",
        PayerCategory.GovernmentScheme => "Government scheme",
        _ => category.ToString(),
    };

    private async Task<IReadOnlyDictionary<Guid, PlanName>> PlansAsync(IReadOnlyCollection<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return wanted.Count == 0
            ? new Dictionary<Guid, PlanName>()
            : await db.Set<TariffPlan>().AsNoTracking().Where(p => wanted.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => new PlanName(p.Name, p.IsActive), ct);
    }

    private async Task<Guid> FacilityAsync(CancellationToken ct) =>
        current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);

    private static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A payer's terms changed: the insurance desk asks, another who may approve prices decides (maker-checker).</summary>
internal sealed class PayerTermsApproval(PayerApprovals payers) : IApprovalAction
{
    public string Kind => PayerApprovals.Kind;

    public string Title => "Payer terms";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => payers.ApplyAsync(request, ct);
}
