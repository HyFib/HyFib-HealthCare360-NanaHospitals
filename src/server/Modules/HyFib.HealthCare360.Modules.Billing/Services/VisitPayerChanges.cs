using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Who pays for a visit: the kind of payer and, for a third party, the payer of the payer master.</summary>
public sealed record VisitPayerTerms(PayerType PayerType, Guid? PayerId)
{
    public static VisitPayerTerms Of(Visit visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        return new(visit.PayerType, visit.PayerId);
    }
}

/// <summary>A visit's payer to be changed: the terms asked, and the terms they replace (a change made meanwhile is not overwritten).</summary>
public sealed record VisitPayerPayload(Guid VisitId, VisitPayerTerms Asked, VisitPayerTerms Replaced);

/// <summary>What the visit's unbilled services come to as priced now, and on the plan of the payer asked.</summary>
public sealed record VisitPayerEffect(int Count, decimal Now, decimal OnAsked, int Cheaper);

/// <summary>A visit's payer waiting for a second person.</summary>
public sealed record PendingVisitPayer(Guid ApprovalId, VisitPayerTerms Asked, string? PayerName, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>A visit's payer as the billing desk sees it: the terms, the payer's name, and a change still waiting.</summary>
public sealed record VisitPayerView(Guid VisitId, string VisitNumber, VisitPayerTerms Terms, string? PayerName, PendingVisitPayer? Pending, uint Version);

/// <summary>The desk's change of a visit's payer: whom it should pay, why, and the visit's version the desk saw.</summary>
public sealed record ChangeVisitPayerRequest(PayerType PayerType, Guid? PayerId, string? Reason, uint Version);

public sealed class ChangeVisitPayerRequestValidator : AbstractValidator<ChangeVisitPayerRequest>
{
    public ChangeVisitPayerRequestValidator()
    {
        RuleFor(x => x.PayerType).IsInEnum();
        RuleFor(x => x.Reason).SafeText(500);
        RuleFor(x => x.Version).SeenVersion();
    }
}

/// <summary>A visit's payer changed at once (with the services priced again), or the approval it waits for.</summary>
public sealed record VisitPayerChangeOutcome(string? Changed, ApprovalRequestDto? Approval);

/// <summary>
/// A visit's payer after registration (BIL-OP): the patient was registered as self-pay and shows an insurance card, or the
/// company that pays is another. The change is allowed while the visit's bill is not final (a draft is discarded first),
/// and prices the visit's unbilled services again on the new payer's plan, as the stay's plan change does
/// (<see cref="ChargeService.RepriceAsync"/>). A third party must stand behind the patient as at registration
/// (<see cref="PayerChoice.Refusal"/>: in use, of the kind chosen, with a verified policy in date). The change is made at
/// once when the payer stays of the same kind and no service gets cheaper; otherwise it is decided by two people
/// (maker-checker): the desk asks, and someone else who holds <see cref="BillingPermissions.DiscountsApprove"/> makes it,
/// only over the terms it replaces, after reading what the services come to on each plan. Either way it is audited.
/// </summary>
public sealed class VisitPayerChanges(
    HospitalDbContext db,
    ChargeService charges,
    TariffService tariffs,
    IPayerDirectory payers,
    ICurrentUser current,
    IAuditLogger audit,
    IApprovals approvals,
    TimeProvider time)
{
    public const string Kind = "billing.visit_payer";

    private const string NoReason = "No reason given.";

    /// <summary>Whether a change is made at once: the payer stays of the same kind and no unbilled service gets cheaper.</summary>
    public static bool AtOnce(VisitPayerTerms before, VisitPayerTerms asked, VisitPayerEffect effect)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(effect);
        return before.PayerType == asked.PayerType && effect.Cheaper == 0 && effect.OnAsked >= effect.Now;
    }

    public async Task<VisitPayerView?> ViewAsync(Guid visitId, CancellationToken ct)
    {
        var visit = await db.Set<Visit>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit is null)
        {
            return null;
        }

        var pending = await PendingAsync(visitId, ct);
        var names = await PayerNamesAsync([visit.PayerId], ct);
        return new VisitPayerView(visit.Id, visit.VisitNumber, VisitPayerTerms.Of(visit), Name(visit.PayerId, names), pending, visit.Version);
    }

    public async Task<Result<VisitPayerChangeOutcome>> ChangeAsync(Guid visitId, ChangeVisitPayerRequest request, CancellationToken ct)
    {
        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit is null)
        {
            return Error.NotFound(nameof(Visit), visitId);
        }

        if (visit.Version != request.Version)
        {
            return Error.Conflict("concurrency", "The visit changed since you opened it. Open it again and retry.");
        }

        var asked = new VisitPayerTerms(request.PayerType, PayerChoice.IsThirdParty(request.PayerType) ? request.PayerId : null);
        var before = VisitPayerTerms.Of(visit);
        if (asked == before)
        {
            return Error.Rule("billing.visit_payer_unchanged", "The visit is already billed to this payer.");
        }

        var open = await OpenAsync(visit, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        var standing = await StandingAsync(visit, asked, ct);
        if (standing is not null)
        {
            return standing;
        }

        if (await PendingAsync(visit.Id, ct) is { } waiting)
        {
            return Error.Conflict("billing.visit_payer_pending", $"A change of this visit's payer asked for by {waiting.RequestedBy} is waiting for approval.");
        }

        var effect = await EffectAsync(visit, asked, ct);
        if (AtOnce(before, asked, effect))
        {
            var words = await ApplyTermsAsync(visit, before, asked, request.Reason, null, ct);
            await db.SaveChangesAsync(ct);
            return new VisitPayerChangeOutcome(words, null);
        }

        var names = await PayerNamesAsync([before.PayerId, asked.PayerId], ct);
        var plans = await PlanNamesAsync(visit, [before, asked], ct);
        List<ApprovalDetail> details =
        [
            new("Visit", visit.VisitNumber),
            new("Paid by", $"{Payer(before, names)} → {Payer(asked, names)}"),
            new("Tariff plan", $"{plans[before]} → {plans[asked]}"),
            new("Unbilled services", $"{effect.Count} service(s): {Rupees(effect.Now)} as priced now"),
            new("On the new payer's plan", $"{Rupees(effect.OnAsked)}: {StayPlanApprovals.Difference(effect.OnAsked, effect.Now)}"),
            new("Why it waits", WhyItWaits(before, asked, effect)),
        ];
        var approval = await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Visit), visit.Id, visit.Version, visit.FacilityId, visit.PatientId),
            new VisitPayerPayload(visit.Id, asked, before),
            Fit($"Visit {visit.VisitNumber} billed to {Payer(asked, names)} instead of {Payer(before, names)}", 500),
            details,
            string.IsNullOrWhiteSpace(request.Reason) ? NoReason : request.Reason.Trim(),
            ct);
        return approval.IsSuccess ? new VisitPayerChangeOutcome(null, approval.Value) : approval.Error!;
    }

    /// <summary>The payer waiting for approval for this visit, if any.</summary>
    public async Task<PendingVisitPayer?> PendingAsync(Guid visitId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Visit) && r.SubjectId == visitId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
        if (request is null)
        {
            return null;
        }

        var asked = ApprovalPayload.Read<VisitPayerPayload>(request.Payload);
        var names = await PayerNamesAsync([asked.Asked.PayerId], ct);
        return new PendingVisitPayer(request.Id, asked.Asked, Name(asked.Asked.PayerId, names), request.RequestedByName, request.RequestedAt);
    }

    /// <summary>
    /// Puts an approved visit on the payer asked, in the approval's transaction, if its payer is still the one the request
    /// replaces, its bill is still not final, and the payer still stands behind the patient.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<VisitPayerPayload>(request.Payload);
        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == asked.VisitId, ct);
        if (visit is null)
        {
            return Error.Rule("billing.visit_gone", "The visit no longer exists, so nothing was set.");
        }

        if (VisitPayerTerms.Of(visit) != asked.Replaced)
        {
            return Error.Rule(TariffApprovals.ChangedCode, $"Visit {visit.VisitNumber}'s payer changed after this was asked for, so nothing was set. Ask again.");
        }

        var open = await OpenAsync(visit, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        if (await StandingAsync(visit, asked.Asked, ct) is { } refused)
        {
            return Error.Rule(refused.Code, refused.Message + " Nothing was set.");
        }

        return await ApplyTermsAsync(visit, asked.Replaced, asked.Asked, request.Reason, request, ct);
    }

    /// <summary>
    /// What the visit's unbilled services come to as priced now and on the plan of the payer asked, each priced on a copy
    /// that is never saved, and how many would be cheaper.
    /// </summary>
    internal async Task<VisitPayerEffect> EffectAsync(Visit visit, VisitPayerTerms asked, CancellationToken ct)
    {
        var now = await UnbilledAsync(visit.Id, tracked: false, ct);
        var onAsked = await UnbilledAsync(visit.Id, tracked: false, ct);
        await charges.RepriceAsync(onAsked, await tariffs.ResolvePlanAsync(asked.PayerType, asked.PayerId, visit.FacilityId, ct), null, ct);
        var before = now.ToDictionary(c => c.Id, c => c.NetAmount);
        var cheaper = onAsked.Count(c => c.NetAmount < before[c.Id]);
        return new VisitPayerEffect(now.Count, now.Sum(c => c.NetAmount), onAsked.Sum(c => c.NetAmount), cheaper);
    }

    private async Task<string> ApplyTermsAsync(
        Visit visit, VisitPayerTerms before, VisitPayerTerms asked, string? reason, ApprovalRequest? approval, CancellationToken ct)
    {
        visit.PayerType = asked.PayerType;
        visit.PayerId = asked.PayerId;
        var plan = await tariffs.ResolvePlanAsync(asked.PayerType, asked.PayerId, visit.FacilityId, ct);
        var unbilled = await UnbilledAsync(visit.Id, tracked: true, ct);
        var priceBefore = unbilled.Sum(c => c.NetAmount);
        await charges.RepriceAsync(unbilled, plan, null, ct);
        var repriced = unbilled.Count(c => c.PriceSource != PriceSource.Override);
        var priceAfter = unbilled.Sum(c => c.NetAmount);
        var names = await PayerNamesAsync([before.PayerId, asked.PayerId], ct);
        var words = $"visit {visit.VisitNumber} billed to {Payer(asked, names)} instead of {Payer(before, names)} on {plan?.Name ?? "the catalogue's own prices"}, "
            + $"{repriced} unbilled service(s) priced again ({Rupees(priceBefore)} → {Rupees(priceAfter)})";
        var who = approval is null
            ? $"changed by {current.DisplayName ?? current.UserName}"
            : $"asked for by {approval.RequestedByName}, approved by {current.DisplayName ?? current.UserName}";
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.visit.payer_changed", Fit($"{words} ({who})", 1000))
        {
            EntityType = nameof(Visit),
            EntityId = visit.Id.ToString(),
            PatientId = visit.PatientId,
            Severity = approval is null ? AuditSeverity.Info : AuditSeverity.Warning,
            Details = new
            {
                before,
                after = asked,
                plan = plan?.Id,
                repriced,
                priceBefore,
                priceAfter,
                reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                approval = approval?.Id,
                requestedBy = approval?.RequestedBy,
            },
        });
        return words;
    }

    /// <summary>The visit may change payer: not cancelled, not an admission's, and with no bill other than a cancelled one.</summary>
    private async Task<Result> OpenAsync(Visit visit, CancellationToken ct)
    {
        if (visit.Status == VisitStatus.Cancelled)
        {
            return Error.Rule("billing.visit_cancelled", "The visit was cancelled; its payer can no longer change.");
        }

        if (await db.Set<IpAccount>().AnyAsync(a => a.VisitId == visit.Id, ct))
        {
            return Error.Rule("billing.visit_admitted", "The patient was admitted from this visit: change the payer on the in-patient account.");
        }

        var bills = await db.Set<Bill>().AsNoTracking()
            .Where(b => b.VisitId == visit.Id && b.AdmissionId == null && b.Status != BillStatus.Cancelled)
            .Select(b => new { b.Status, b.BillNumber })
            .ToListAsync(ct);
        if (bills.FirstOrDefault(b => b.Status != BillStatus.Draft) is { } final)
        {
            return Error.Rule("billing.visit_bill_final", $"Bill {final.BillNumber} of this visit is final; its payer can no longer change. Cancel the bill first if it is wrong.");
        }

        if (bills.Count > 0)
        {
            return Error.Rule("billing.draft_bill_exists", "Discard the visit's draft bill before changing its payer, so its services are priced again.");
        }

        return Result.Success();
    }

    private async Task<Error?> StandingAsync(Visit visit, VisitPayerTerms asked, CancellationToken ct)
    {
        var today = BillingTime.Today(time);
        var standing = asked.PayerId is { } payerId ? await payers.StandingAsync(visit.PatientId, payerId, today, ct) : null;
        return PayerChoice.Refusal(asked.PayerType, asked.PayerId, standing);
    }

    private Task<List<Charge>> UnbilledAsync(Guid visitId, bool tracked, CancellationToken ct)
    {
        var query = db.Set<Charge>().Where(c => c.VisitId == visitId && c.AdmissionId == null && c.Status == ChargeStatus.Unbilled && c.BillId == null);
        return (tracked ? query : query.AsNoTracking()).ToListAsync(ct);
    }

    private async Task<Dictionary<VisitPayerTerms, string>> PlanNamesAsync(Visit visit, IEnumerable<VisitPayerTerms> terms, CancellationToken ct)
    {
        var names = new Dictionary<VisitPayerTerms, string>();
        foreach (var t in terms.Distinct())
        {
            names[t] = (await tariffs.ResolvePlanAsync(t.PayerType, t.PayerId, visit.FacilityId, ct))?.Name ?? "the catalogue's own prices";
        }

        return names;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> PayerNamesAsync(IReadOnlyCollection<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return wanted.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<Payer>().AsNoTracking().Where(p => wanted.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
    }

    private static string WhyItWaits(VisitPayerTerms before, VisitPayerTerms asked, VisitPayerEffect effect) =>
        before.PayerType != asked.PayerType
            ? $"The payer changes kind ({KindOf(before.PayerType)} → {KindOf(asked.PayerType)})"
            : $"{effect.Cheaper} service(s) get cheaper on the new payer's plan";

    private static string KindOf(PayerType type) => type switch
    {
        PayerType.Self => "self-pay",
        PayerType.Insurance => "insurer",
        PayerType.Corporate => "company",
        PayerType.GovernmentScheme => "government scheme",
        PayerType.Staff => "staff",
        _ => "charity",
    };

    private static string? Name(Guid? id, IReadOnlyDictionary<Guid, string> names) => id is { } payerId ? names.GetValueOrDefault(payerId) : null;

    private static string Payer(VisitPayerTerms terms, IReadOnlyDictionary<Guid, string> names) =>
        terms.PayerId is { } payerId ? names.GetValueOrDefault(payerId, "its payer") : KindOf(terms.PayerType);

    private static string Rupees(decimal amount) => "₹" + Money.FormatInr(amount);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>
/// A visit's payer changed to another kind of payer, or to one that prices its services lower: the billing desk asks,
/// another who may approve discounts decides (maker-checker).
/// </summary>
internal sealed class VisitPayerApproval(VisitPayerChanges visits) : IApprovalAction
{
    public string Kind => VisitPayerChanges.Kind;

    public string Title => "Visit's payer changed";

    public string ApprovePermission => BillingPermissions.DiscountsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => visits.ApplyAsync(request, ct);
}
