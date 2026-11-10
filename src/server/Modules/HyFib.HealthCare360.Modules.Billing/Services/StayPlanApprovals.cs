using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Who pays for a stay, under which policy, and the tariff plan that prices its services.</summary>
public sealed record StayPayerTerms(PayerType PayerType, Guid? PayerId, Guid? PolicyId, Guid? TariffPlanId)
{
    public static StayPayerTerms Of(IpAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new(account.PayerType, account.PayerId, account.PolicyId, account.TariffPlanId);
    }

    public bool SamePayer(StayPayerTerms other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return PayerType == other.PayerType && PayerId == other.PayerId;
    }
}

/// <summary>
/// A stay to be billed on a plan other than its payer's: the terms asked, whether its unbilled services are priced again,
/// the terms they replace (a change made meanwhile is not overwritten), and its payer's plan.
/// </summary>
public sealed record StayPlanPayload(Guid AccountId, StayPayerTerms Asked, bool RepriceUnbilledCharges, StayPayerTerms Replaced, Guid? PayerPlanId);

/// <summary>A stay's payer and plan waiting for a second person.</summary>
public sealed record PendingStayPlan(
    Guid ApprovalId, StayPayerTerms Asked, string? TariffPlanName, bool RepriceUnbilledCharges, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// A stay is billed on its payer's plan (the one billing resolves for its payer at admission). Billing it on any other plan
/// (a concession, or a dearer plan) is decided by two people (maker-checker): the billing desk asks, with the payer and
/// policy asked with it, and someone else who holds <see cref="BillingPermissions.DiscountsApprove"/> makes the change,
/// only over the terms it replaces. The approver reads what the stay's unbilled services come to on each plan.
/// </summary>
public sealed class StayPlanApprovals(
    HospitalDbContext db, ChargeService charges, TariffService tariffs, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "billing.ip_plan";

    private const string NoReason = "No reason given.";

    /// <summary>
    /// Whether a stay's payer terms change at once: when they put it on its payer's plan, or keep the plan it has while its
    /// payer stays the same. Any other plan waits for a second person.
    /// </summary>
    public static bool AtOnce(StayPayerTerms before, StayPayerTerms asked, Guid? payerPlanId)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(asked);
        return asked.TariffPlanId == payerPlanId || (asked.TariffPlanId == before.TariffPlanId && asked.SamePayer(before));
    }

    /// <summary>Asks a second person to put <paramref name="account"/> on <paramref name="asked"/>.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(
        IpAccount account, StayPayerTerms asked, bool reprice, Guid? payerPlanId, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(asked);
        var replaced = StayPayerTerms.Of(account);
        var plans = await PlanNamesAsync([replaced.TariffPlanId, asked.TariffPlanId, payerPlanId], ct);
        var payers = await PayerNamesAsync([replaced.PayerId, asked.PayerId], ct);
        List<ApprovalDetail> details = [new("Admission", account.AdmissionNumber)];
        if (!asked.SamePayer(replaced))
        {
            details.Add(new("Paid by", $"{Payer(replaced, payers)} → {Payer(asked, payers)}"));
        }

        details.Add(new("Tariff plan", $"{Plan(replaced.TariffPlanId, plans)} → {Plan(asked.TariffPlanId, plans)}"));
        details.Add(new("Its payer's plan", $"{Plan(payerPlanId, plans)}: what {Payer(asked, payers)} patients are priced on"));
        if (reprice)
        {
            var effect = await EffectAsync(account, payerPlanId, asked.TariffPlanId, ct);
            details.Add(new("Unbilled services", $"{effect.Count} service(s): {Rupees(effect.Now)} as priced now"));
            details.Add(new("On its payer's plan", $"{Rupees(effect.OnPayerPlan)} on {Plan(payerPlanId, plans)}"));
            details.Add(new("On the plan asked", $"{Rupees(effect.OnAsked)} on {Plan(asked.TariffPlanId, plans)}: {Difference(effect.OnAsked, effect.OnPayerPlan)} against its payer's plan"));
        }
        else
        {
            details.Add(new("Unbilled services", $"Keep their prices; services charged after approval are priced on {Plan(asked.TariffPlanId, plans)}"));
        }

        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(IpAccount), account.Id, account.Version, account.FacilityId, account.PatientId),
            new StayPlanPayload(account.Id, asked, reprice, replaced, payerPlanId),
            Fit($"Admission {account.AdmissionNumber} billed on {Plan(asked.TariffPlanId, plans)} instead of {Plan(payerPlanId, plans)}, its payer's plan", 500),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>The payer and plan waiting for approval for this stay, if any.</summary>
    public async Task<PendingStayPlan?> PendingAsync(Guid accountId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(IpAccount) && r.SubjectId == accountId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
        if (request is null)
        {
            return null;
        }

        var asked = ApprovalPayload.Read<StayPlanPayload>(request.Payload);
        var planName = asked.Asked.TariffPlanId is { } planId ? await db.Set<TariffPlan>().Where(p => p.Id == planId).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        return new PendingStayPlan(request.Id, asked.Asked, planName, asked.RepriceUnbilledCharges, request.RequestedByName, request.RequestedAt);
    }

    /// <summary>
    /// Puts an approved stay on the payer and plan asked, in the approval's transaction, if its payer terms are still the
    /// ones the request replaces, and prices its unbilled services again when that was asked.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<StayPlanPayload>(request.Payload);
        var account = await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == asked.AccountId, ct);
        if (account is null)
        {
            return Error.Rule("billing.ip_account_gone", "The in-patient account no longer exists, so nothing was set.");
        }

        if (!account.IsActive)
        {
            return Error.Rule("billing.ip_account_inactive", "The patient has been discharged; the account's payer can no longer change.");
        }

        var before = StayPayerTerms.Of(account);
        if (before != asked.Replaced)
        {
            return Error.Rule(TariffApprovals.ChangedCode, $"Admission {account.AdmissionNumber}'s payer or plan changed after this was asked for, so nothing was set. Ask again.");
        }

        var plan = await tariffs.FindPlanAsync(asked.Asked.TariffPlanId, ct);
        if (plan is not { IsActive: true })
        {
            return Error.Rule("billing.plan_out_of_use", "The tariff plan asked for is out of use now, so nothing was set.");
        }

        if (asked.Asked.PayerId is { } payerId && !await db.Set<Payer>().AnyAsync(p => p.Id == payerId && p.IsActive, ct))
        {
            return Error.Rule("billing.payer_out_of_use", "The payer asked for is out of use now, so nothing was set.");
        }

        if (asked.RepriceUnbilledCharges && await db.Set<Bill>().AnyAsync(b => b.AdmissionId == account.AdmissionId && b.Status == BillStatus.Draft, ct))
        {
            return Error.Rule("billing.draft_bill_exists", "Discard the admission's draft bill before re-pricing its services.");
        }

        account.PayerType = asked.Asked.PayerType;
        account.PayerId = asked.Asked.PayerId;
        account.PolicyId = asked.Asked.PolicyId;
        account.TariffPlanId = asked.Asked.TariffPlanId;
        var repriced = 0;
        if (asked.RepriceUnbilledCharges)
        {
            var unbilled = await db.Set<Charge>()
                .Where(c => c.AdmissionId == account.AdmissionId && c.Status == ChargeStatus.Unbilled && c.BillId == null)
                .ToListAsync(ct);
            await charges.RepriceAsync(unbilled, plan, null, ct);
            repriced = unbilled.Count(c => c.PriceSource != PriceSource.Override);
        }

        var words = $"admission {account.AdmissionNumber} billed on {plan.Name}{(asked.RepriceUnbilledCharges ? $", {repriced} unbilled service(s) priced again" : string.Empty)}";
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.ip.plan_changed",
            Fit($"{words} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(IpAccount),
            EntityId = account.Id.ToString(),
            PatientId = account.PatientId,
            Severity = AuditSeverity.Warning,
            Details = new { before, after = asked.Asked, payerPlan = asked.PayerPlanId, repriced, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return words;
    }

    /// <summary>
    /// What the stay's unbilled services come to as priced now, on its payer's plan and on the plan asked: each priced on a
    /// copy that is never saved.
    /// </summary>
    internal async Task<(int Count, decimal Now, decimal OnPayerPlan, decimal OnAsked)> EffectAsync(
        IpAccount account, Guid? payerPlanId, Guid? askedPlanId, CancellationToken ct)
    {
        var now = await UnbilledAsync(account, ct);
        var onPayerPlan = await UnbilledAsync(account, ct);
        await charges.RepriceAsync(onPayerPlan, await tariffs.FindPlanAsync(payerPlanId, ct), null, ct);
        var onAsked = await UnbilledAsync(account, ct);
        await charges.RepriceAsync(onAsked, await tariffs.FindPlanAsync(askedPlanId, ct), null, ct);
        return (now.Count, now.Sum(c => c.NetAmount), onPayerPlan.Sum(c => c.NetAmount), onAsked.Sum(c => c.NetAmount));
    }

    // Copies (not tracked): pricing them again changes nothing saved.
    private Task<List<Charge>> UnbilledAsync(IpAccount account, CancellationToken ct) =>
        db.Set<Charge>().AsNoTracking()
            .Where(c => c.AdmissionId == account.AdmissionId && c.Status == ChargeStatus.Unbilled && c.BillId == null)
            .ToListAsync(ct);

    private async Task<IReadOnlyDictionary<Guid, string>> PlanNamesAsync(IReadOnlyCollection<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return wanted.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<TariffPlan>().AsNoTracking().Where(p => wanted.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> PayerNamesAsync(IReadOnlyCollection<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return wanted.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<Payer>().AsNoTracking().Where(p => wanted.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
    }

    private static string Plan(Guid? id, IReadOnlyDictionary<Guid, string> plans) =>
        id is { } planId ? plans.GetValueOrDefault(planId, "a tariff plan") : "the catalogue's own prices";

    private static string Payer(StayPayerTerms terms, IReadOnlyDictionary<Guid, string> payers) =>
        terms.PayerId is { } payerId ? payers.GetValueOrDefault(payerId, "its payer") : terms.PayerType == PayerType.Self ? "self-pay" : terms.PayerType.ToString();

    /// <summary>A difference in rupees and per cent, signed: "−₹15,000.00 (−33.3%)".</summary>
    internal static string Difference(decimal amount, decimal against)
    {
        var difference = amount - against;
        var sign = difference > 0 ? "+" : difference < 0 ? "−" : string.Empty;
        var percent = against == 0m ? string.Empty : $" ({sign}{Math.Abs(difference / against * 100m).ToString("0.#", CultureInfo.InvariantCulture)}%)";
        return $"{sign}{Rupees(Math.Abs(difference))}{percent}";
    }

    private static string Rupees(decimal amount) => "₹" + Money.FormatInr(amount);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>
/// A stay billed on a plan other than its payer's: the billing desk asks, another who may approve discounts decides
/// (maker-checker).
/// </summary>
internal sealed class StayPlanApproval(StayPlanApprovals stays) : IApprovalAction
{
    public string Kind => StayPlanApprovals.Kind;

    public string Title => "Stay billed on another plan";

    public string ApprovePermission => BillingPermissions.DiscountsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => stays.ApplyAsync(request, ct);
}
