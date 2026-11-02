using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// What a sharing rule decides about what doctors earn (DFA-01): whose lines (one doctor's, or every doctor's), which
/// (a service, a category, or every service, for a payer type or any), how much (a percentage of the billed value or a
/// fixed amount a unit), when, and whether it is in use. Its notes decide nothing.
/// </summary>
public sealed record ShareRuleTerms(
    Guid? PractitionerId,
    Guid? ServiceItemId,
    ServiceCategory? Category,
    PayerType? PayerType,
    ShareMethod Method,
    decimal Value,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive)
{
    public static ShareRuleTerms Of(DoctorShareRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new(rule.PractitionerId, rule.ServiceItemId, rule.Category, rule.PayerType, rule.Method, rule.Value, rule.EffectiveFrom, rule.EffectiveTo, rule.IsActive);
    }

    public static ShareRuleTerms Of(SaveShareRuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(request.PractitionerId, request.ServiceItemId, request.Category, request.PayerType, request.Method, request.Value, request.EffectiveFrom,
            request.EffectiveTo, request.IsActive);
    }

    /// <summary>A new rule until a second person approves it: as asked, but out of use, so it shares nothing.</summary>
    public ShareRuleTerms OutOfUse() => this with { IsActive = false };

    public ShareRuleView ToView(Guid id) => new(id, PractitionerId, ServiceItemId, Category, PayerType, Method, Value, EffectiveFrom, EffectiveTo);

    public void ApplyTo(DoctorShareRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rule.PractitionerId = PractitionerId;
        rule.ServiceItemId = ServiceItemId;
        rule.Category = Category;
        rule.PayerType = PayerType;
        rule.Method = Method;
        rule.Value = Value;
        rule.EffectiveFrom = EffectiveFrom;
        rule.EffectiveTo = EffectiveTo;
        rule.IsActive = IsActive;
    }
}

/// <summary>A sharing rule's terms asked for, and the terms they replace (a change made meanwhile is not overwritten).</summary>
public sealed record ShareRulePayload(Guid RuleId, ShareRuleTerms Asked, ShareRuleTerms Replaced);

/// <summary>A sharing rule's terms waiting for a second person, as the rules list and the rule's form show them.</summary>
public sealed record PendingShareRule(Guid ApprovalId, ShareRuleTerms Asked, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// A doctor's share of what is billed changes only when two people agree (maker-checker, DFA-01): whoever keeps the
/// sharing rules asks, and someone else who holds <see cref="PlatformPermissions.PricesApprove"/> makes the change, only
/// over the terms it replaces. Every change of terms waits, taking a rule out of use included: the most specific rule
/// in force decides a line's share, so the lines of a rule taken out of use fall back to a broader rule, which may
/// share more. A new rule is made out of use until its terms are approved. The approver is told whose lines it shares,
/// how, from when (a backdated rule shares the bills finalized from now for services since then), and what it would have
/// changed on the last <see cref="ImpactDays"/> days' bill lines.
/// </summary>
public sealed class ShareRuleApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "doctorfees.rule";

    /// <summary>How many days of bill lines the approver is shown the change on.</summary>
    public const int ImpactDays = 90;

    private const string NoReason = "No reason given.";

    /// <summary>Asks a second person to set <paramref name="rule"/>'s terms to <paramref name="asked"/>.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(DoctorShareRule rule, ShareRuleTerms replaced, ShareRuleTerms asked, bool isNew, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(asked);
        var names = await NamesAsync(replaced, asked, ct);
        var today = BillingTime.Today(time);
        List<ApprovalDetail> details = [.. Changes(replaced, asked, isNew, names).Select(c => new ApprovalDetail(c.Label, c.Value))];
        if (asked.IsActive && asked.EffectiveFrom < today)
        {
            details.Add(new("Backdated", $"In force from {Date(asked.EffectiveFrom)}, {today.DayNumber - asked.EffectiveFrom.DayNumber} day(s) before today: bills finalized from now for services since then are shared at it"));
        }

        var impact = await ImpactAsync(rule.Id, replaced, asked, today, ct);
        details.Add(new($"Last {ImpactDays} days", impact.Lines == 0
            ? $"No bill line of the last {ImpactDays} days would be shared differently"
            : $"{impact.Lines.ToString("N0", CultureInfo.InvariantCulture)} bill line(s) would be shared differently: {Rupees(impact.Before)} → {Rupees(impact.After)} ({Signed(impact.Change)})"));

        var summary = isNew
            ? $"New sharing rule for {Doctor(asked.PractitionerId, names)}, {Scope(asked, names)}: {Share(asked)} {Period(asked)}"
            : $"Sharing rule for {Doctor(asked.PractitionerId, names)}, {Scope(asked, names)}: {string.Join("; ", Changes(replaced, asked, false, names).Select(c => c.Words))}";
        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(DoctorShareRule), rule.Id, rule.Version, await FacilityAsync(ct), null),
            new ShareRulePayload(rule.Id, asked, replaced),
            Fit(summary, 300),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>The rules with a change waiting for approval.</summary>
    public async Task<List<Guid>> WaitingAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(DoctorShareRule) && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .Select(r => r.SubjectId).Distinct().ToListAsync(ct);
    }

    /// <summary>The terms waiting for approval for these rules, by rule.</summary>
    public async Task<IReadOnlyDictionary<Guid, PendingShareRule>> PendingAsync(IReadOnlyCollection<Guid> ruleIds, CancellationToken ct)
    {
        if (ruleIds.Count == 0)
        {
            return new Dictionary<Guid, PendingShareRule>();
        }

        var now = time.GetUtcNow();
        var requests = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(DoctorShareRule) && ruleIds.Contains(r.SubjectId) && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .Select(r => new { r.Id, r.SubjectId, r.Payload, r.RequestedByName, r.RequestedAt })
            .ToListAsync(ct);
        return requests.GroupBy(r => r.SubjectId).ToDictionary(
            g => g.Key,
            g => g.OrderByDescending(r => r.RequestedAt).Select(r => new PendingShareRule(r.Id, ApprovalPayload.Read<ShareRulePayload>(r.Payload).Asked, r.RequestedByName, r.RequestedAt)).First());
    }

    /// <summary>Sets an approved rule's terms, in the approval's transaction, if they are still the ones the request replaces.</summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ShareRulePayload>(request.Payload);
        var rule = await db.Set<DoctorShareRule>().FirstOrDefaultAsync(r => r.Id == asked.RuleId, ct);
        if (rule is null)
        {
            return Error.Rule("doctorfees.rule_gone", "The sharing rule no longer exists, so nothing was set.");
        }

        var before = ShareRuleTerms.Of(rule);
        if (before != asked.Replaced)
        {
            return Error.Rule(TariffApprovals.ChangedCode, "The sharing rule changed after this was asked for, so nothing was set. Ask again.");
        }

        asked.Asked.ApplyTo(rule);
        var names = await NamesAsync(before, asked.Asked, ct);
        var words = string.Join("; ", Changes(before, asked.Asked, false, names).Select(c => c.Words));
        audit.Add(new AuditEntry(AuditCategories.Admin, "doctorfees.rule_changed",
            Fit($"Sharing rule for {Doctor(asked.Asked.PractitionerId, names)}, {Scope(asked.Asked, names)}: {words} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(DoctorShareRule),
            EntityId = rule.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { before, after = asked.Asked, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return words;
    }

    /// <summary>
    /// What the change would have done to the doctors' share of the last <see cref="ImpactDays"/> days' bill lines that it
    /// can reach, each shared as on the day the change takes effect, under the rules in use with the rule as it is and as
    /// asked.
    /// </summary>
    private async Task<ShareImpact> ImpactAsync(Guid ruleId, ShareRuleTerms replaced, ShareRuleTerms asked, DateOnly today, CancellationToken ct)
    {
        Guid?[] doctors = [replaced.PractitionerId, asked.PractitionerId];
        var everyDoctor = doctors.Any(d => d is null);
        var ids = doctors.OfType<Guid>().Distinct().ToList();
        var since = BillingTime.StartOfDay(today.AddDays(-ImpactDays));
        var lines = await (
                from l in db.Set<BillLine>().AsNoTracking()
                join b in db.Set<Bill>().AsNoTracking() on l.BillId equals b.Id
                where b.BillNumber != null && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid || b.Status == BillStatus.Paid)
                    && l.PractitionerId != null && l.TaxableAmount > 0 && l.ServiceDate >= since
                    && (everyDoctor || ids.Contains(l.PractitionerId!.Value))
                select new { PractitionerId = l.PractitionerId!.Value, l.ServiceItemId, l.Category, b.PayerType, l.Quantity, l.TaxableAmount })
            .ToListAsync(ct);
        var others = (await db.Set<DoctorShareRule>().AsNoTracking()
                .Where(r => r.IsActive && r.Id != ruleId && (everyDoctor || r.PractitionerId == null || ids.Contains(r.PractitionerId!.Value)))
                .ToListAsync(ct))
            .Select(r => r.ToView())
            .ToList();
        var at = asked.EffectiveFrom > today ? asked.EffectiveFrom : today;
        List<ShareRuleView> before = replaced.IsActive ? [.. others, replaced.ToView(ruleId)] : others;
        List<ShareRuleView> after = asked.IsActive ? [.. others, asked.ToView(ruleId)] : others;
        return ShareRuleImpact.Of(
            lines.Select(l => new ShareLine(l.PractitionerId, l.ServiceItemId, l.Category, l.PayerType, l.Quantity, l.TaxableAmount, at)), before, after);
    }

    /// <summary>The names the approver reads changes in: doctors and services.</summary>
    internal sealed record Names(IReadOnlyDictionary<Guid, string> Doctors, IReadOnlyDictionary<Guid, string> Services);

    /// <summary>What changes from <paramref name="from"/> to <paramref name="to"/>: a detail row and its words, each.</summary>
    internal static IEnumerable<(string Label, string Value, string Words)> Changes(ShareRuleTerms from, ShareRuleTerms to, bool isNew, Names names)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(names);
        if (isNew)
        {
            yield return ("Doctor", Doctor(to.PractitionerId, names), $"for {Doctor(to.PractitionerId, names)}");
            yield return ("Applies to", Scope(to, names), $"applies to {Scope(to, names)}");
            yield return ("Share", Share(to), $"share {Share(to)}");
            yield return ("In force", Period(to), $"in force {Period(to)}");
            yield break;
        }

        if (from.IsActive != to.IsActive)
        {
            yield return to.IsActive
                ? ("In use", "Put in use: bills finalized from now are shared at it", "put in use")
                : ("In use", "Taken out of use: the bill lines it shares fall back to the next rule that applies, which may share more or less", "taken out of use");
        }

        if (from.PractitionerId != to.PractitionerId)
        {
            var value = $"{Doctor(from.PractitionerId, names)} → {Doctor(to.PractitionerId, names)}";
            yield return ("Doctor", value, $"for {Doctor(to.PractitionerId, names)}");
        }

        if (from.ServiceItemId != to.ServiceItemId || from.Category != to.Category || from.PayerType != to.PayerType)
        {
            var value = $"{Scope(from, names)} → {Scope(to, names)}";
            yield return ("Applies to", value, $"applies to {Scope(to, names)}");
        }

        if (from.Method != to.Method || from.Value != to.Value)
        {
            var value = $"{Share(from)} → {Share(to)}";
            yield return ("Share", value, $"share {value}");
        }

        if (from.EffectiveFrom != to.EffectiveFrom || from.EffectiveTo != to.EffectiveTo)
        {
            var value = $"{Period(from)} → {Period(to)}";
            yield return ("In force", value, $"in force {Period(to)}");
        }
    }

    private async Task<Names> NamesAsync(ShareRuleTerms from, ShareRuleTerms to, CancellationToken ct)
    {
        var doctorIds = new[] { from.PractitionerId, to.PractitionerId }.OfType<Guid>().Distinct().ToList();
        var serviceIds = new[] { from.ServiceItemId, to.ServiceItemId }.OfType<Guid>().Distinct().ToList();
        var doctors = doctorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<Practitioner>().AsNoTracking().Where(p => doctorIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var services = serviceIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<ServiceItem>().AsNoTracking().Where(s => serviceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => $"{s.Code} {s.Name}", ct);
        return new Names(doctors, services);
    }

    private async Task<Guid> FacilityAsync(CancellationToken ct) =>
        current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);

    private static string Doctor(Guid? id, Names names) =>
        id is { } doctor ? names.Doctors.GetValueOrDefault(doctor, "a doctor") : "every doctor (the hospital's default)";

    private static string Scope(ShareRuleTerms terms, Names names)
    {
        var what = terms.ServiceItemId is { } service
            ? names.Services.GetValueOrDefault(service, "a service")
            : terms.Category is { } category ? $"every {Words(category.ToString())} service" : "every service";
        return terms.PayerType is { } payer ? $"{what}, for {Words(payer.ToString())} patients" : what;
    }

    private static string Share(ShareRuleTerms terms) => terms.Method == ShareMethod.Percentage
        ? $"{terms.Value.ToString("0.##", CultureInfo.InvariantCulture)}% of the billed value"
        : $"{Rupees(terms.Value)} a unit";

    private static string Period(ShareRuleTerms terms) =>
        $"from {Date(terms.EffectiveFrom)}{(terms.EffectiveTo is { } to ? $" to {Date(to)}" : string.Empty)}";

    private static string Date(DateOnly date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>An enum value in words: "BedCharge" → "bed charge".</summary>
    private static string Words(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string Rupees(decimal amount) => "₹" + Money.FormatInr(amount);

    private static string Signed(decimal amount) => (amount > 0 ? "+" : amount < 0 ? "−" : string.Empty) + Rupees(Math.Abs(amount));

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A sharing rule's terms changed: whoever keeps the rules asks, another who may approve prices decides (maker-checker).</summary>
internal sealed class ShareRuleApproval(ShareRuleApprovals rules) : IApprovalAction
{
    public string Kind => ShareRuleApprovals.Kind;

    public string Title => "Doctor sharing rule";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => rules.ApplyAsync(request, ct);
}
