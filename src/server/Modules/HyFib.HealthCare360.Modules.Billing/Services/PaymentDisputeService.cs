using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Chargebacks (<see cref="PaymentDispute"/>): a case is opened when a gateway settlement takes money back on a payment
/// HC360 recorded, when the acquirer's report carries a chargeback on a terminal payment HC360 matched, or by an
/// accountant from the bank's notice. Accountants record what the notice says, the evidence sent and a win; accepting
/// the loss asks for the receipt to be reversed, which a second person who approves reversals makes. Those holding
/// <see cref="BillingPermissions.GatewayReconcile"/> are told of each new case and reminded daily of a deadline near or
/// past. Every change is audited.
/// </summary>
public sealed class PaymentDisputeService(
    HospitalDbContext db,
    INumberSeries numbers,
    IApprovals approvals,
    ReversalService reversals,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const int MaxPageSize = 100;

    /// <summary>The modes a bank can take money back from through a chargeback.</summary>
    private static readonly PaymentMode[] DisputableModes = [PaymentMode.Card, PaymentMode.Upi, PaymentMode.PaymentGateway, PaymentMode.NetBanking, PaymentMode.Wallet];

    /// <summary>
    /// A gateway settlement's adjustment on a payment HC360 recorded: money taken back opens a case (or joins the case an
    /// accountant opened from the notice); the money taken coming back wins the open case. Returns why the line needs no
    /// exception, or null when it does (a payment HC360 does not know, money added that no case explains, or a return of
    /// part of what was taken: the case stays open for the rest).
    /// </summary>
    internal async Task<string?> TakeGatewayAdjustmentAsync(GatewaySettlementLine line, string settlementId, CancellationToken ct)
    {
        if (line.Type != PaymentTransactionType.Adjustment || line.PaymentId is not { } paymentId || line.Gross == 0)
        {
            return null;
        }

        var payment = await PaymentAsync(line.FacilityId, p => p.Mode == PaymentMode.PaymentGateway && p.ExternalId == paymentId, ct);
        if (payment is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (line.Gross > 0)
        {
            // The settlement is read again on every fetch of its day: a return that already won its case is followed.
            var open = await Cases(line.FacilityId).FirstOrDefaultAsync(d => d.ReturnLineId == line.Id, ct)
                       ?? await Cases(line.FacilityId).FirstOrDefaultAsync(d => d.ReceiptPaymentId == payment.Value.Payment.Id && d.Status != PaymentDisputeStatus.Won && d.Status != PaymentDisputeStatus.Lost, ct);
            // Only the money taken coming back wins the case by itself. Part of it (or more) does not: the case stays open
            // for what is still taken back, and the return is an exception for a person to put against it.
            if (open is null || (open.ReturnLineId != line.Id && line.Gross != open.Amount))
            {
                return null;
            }

            var returned = $"₹{Money.FormatInr(line.Gross)}";
            if (open.WonByReturn($"The gateway returned {returned} in settlement {settlementId} ({line.EntityId}).", line.Id, now))
            {
                Audit(open, "billing.dispute.won", $"Chargeback {open.Number} won: the gateway returned {returned} in settlement {settlementId}");
            }

            return $"The gateway returned the money of chargeback {open.Number}.";
        }

        var dispute = await Cases(line.FacilityId).FirstOrDefaultAsync(d => d.GatewaySettlementLineId == line.Id, ct)
                      ?? await JoinNoticeAsync(payment.Value.Payment.Id, line.FacilityId, d => d.GatewaySettlementLineId = line.Id, ct)
                      ?? await OpenAsync(payment.Value.Receipt, payment.Value.Payment, PaymentDisputeSource.Gateway, -line.Gross, paymentId, null, d => d.GatewaySettlementLineId = line.Id, ct);
        return $"Followed as chargeback {dispute.Number}.";
    }

    /// <summary>
    /// A chargeback in the acquirer's report on a terminal payment HC360 matched (the sale's transaction with the same
    /// RRN, or approval code at the same terminal, names the receipt): it opens a case, or joins the one opened from the
    /// notice. A chargeback on a payment HC360 did not match stays to look at.
    /// </summary>
    internal async Task TakeTerminalChargebacksAsync(IReadOnlyList<PosSettlementLine> lines, CancellationToken ct)
    {
        foreach (var line in lines.Where(l => l.Type == PosSettlementLineType.Adjustment && l.DisputeId is null && l.ReviewedAt is null && l.WithdrawnOn is null))
        {
            // The sale is in the same report (matched a moment ago, not saved yet) or in one imported before.
            var sale = lines.FirstOrDefault(s => s.FacilityId == line.FacilityId && s.Type == PosSettlementLineType.Sale && s.WithdrawnOn == null && s.ReceiptPaymentId != null
                                                 && ((line.Rrn != null && s.Rrn == line.Rrn)
                                                     || (line.Rrn == null && line.ApprovalCode != null && s.ApprovalCode == line.ApprovalCode && s.TerminalId == line.TerminalId)))
                           ?.ReceiptPaymentId
                       ?? await db.Set<PosSettlementLine>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                           .Where(s => s.FacilityId == line.FacilityId && s.Type == PosSettlementLineType.Sale && s.WithdrawnOn == null && s.ReceiptPaymentId != null
                                       && ((line.Rrn != null && s.Rrn == line.Rrn)
                                           || (line.Rrn == null && line.ApprovalCode != null && s.ApprovalCode == line.ApprovalCode && s.TerminalId == line.TerminalId)))
                           .Select(s => s.ReceiptPaymentId)
                           .FirstOrDefaultAsync(ct);
            if (sale is not { } receiptPaymentId)
            {
                continue;
            }

            var payment = await PaymentAsync(line.FacilityId, p => p.Id == receiptPaymentId, ct);
            if (payment is null)
            {
                continue;
            }

            var dispute = await Cases(line.FacilityId).FirstOrDefaultAsync(d => d.PosSettlementLineId == line.Id, ct)
                          ?? await JoinNoticeAsync(receiptPaymentId, line.FacilityId, d => d.PosSettlementLineId = line.Id, ct)
                          ?? await OpenAsync(payment.Value.Receipt, payment.Value.Payment, PaymentDisputeSource.Terminal, line.Amount, line.Rrn ?? line.ApprovalCode, null,
                              d => d.PosSettlementLineId = line.Id, ct);
            line.DisputeId = dispute.Id;
        }
    }

    /// <summary>A chargeback the bank or the gateway gave notice of, before a settlement carried it.</summary>
    public async Task<Result<PaymentDisputeDto>> OpenFromNoticeAsync(Guid facilityId, OpenPaymentDisputeRequest request, CancellationToken ct)
    {
        var number = request.ReceiptNumber.Trim();
        var receipt = await db.Set<Receipt>().AsNoTracking().FirstOrDefaultAsync(r => r.FacilityId == facilityId && r.ReceiptNumber == number, ct);
        if (receipt is null)
        {
            return Error.Field("receiptNumber", $"No receipt {number} in this facility.");
        }

        var lines = await db.Set<ReceiptPayment>().AsNoTracking()
            .Where(p => p.ReceiptId == receipt.Id && DisputableModes.Contains(p.Mode))
            .ToListAsync(ct);
        var payment = request.PaymentId is { } id ? lines.FirstOrDefault(p => p.Id == id) : lines.Count == 1 ? lines[0] : null;
        if (payment is null)
        {
            return lines.Count == 0
                ? Error.Rule("billing.dispute_not_card", $"Receipt {receipt.ReceiptNumber} has no card, UPI or online payment for a bank to take back.")
                : Error.Field("paymentId", $"Receipt {receipt.ReceiptNumber} has more than one card, UPI or online payment: choose the one the bank took back.");
        }

        if (request.Amount > payment.Amount)
        {
            return Error.Field("amount", $"The payment was ₹{Money.FormatInr(payment.Amount)}: a chargeback cannot take back more.");
        }

        if (await Cases(facilityId).AnyAsync(d => d.ReceiptPaymentId == payment.Id && d.Status != PaymentDisputeStatus.Won && d.Status != PaymentDisputeStatus.Lost, ct))
        {
            return Error.Conflict("billing.dispute_open", $"A chargeback on this payment of receipt {receipt.ReceiptNumber} is open already.");
        }

        var described = (Result?)null;
        var dispute = await OpenAsync(receipt, payment, PaymentDisputeSource.Notice, request.Amount, payment.ExternalId ?? payment.Reference, UserName(),
            d => described = d.Describe(request.ReasonCode, request.Reason, request.RespondBy), ct);
        if (described is { IsFailure: true })
        {
            return described.Error!;
        }

        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([dispute], ct))[0];
    }

    /// <summary>
    /// The card, UPI and online payment lines of a receipt of the facility, for choosing the one the bank took back, each
    /// saying whether a case on it is open already.
    /// </summary>
    public async Task<Result<List<DisputablePaymentDto>>> PaymentLinesAsync(Guid facilityId, string receiptNumber, CancellationToken ct)
    {
        var number = receiptNumber.Trim();
        var receipt = await db.Set<Receipt>().AsNoTracking().FirstOrDefaultAsync(r => r.FacilityId == facilityId && r.ReceiptNumber == number, ct);
        if (receipt is null)
        {
            return Error.NotFound("Receipt", number);
        }

        var lines = await db.Set<ReceiptPayment>().AsNoTracking()
            .Where(p => p.ReceiptId == receipt.Id && DisputableModes.Contains(p.Mode))
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
        var ids = lines.Select(p => p.Id).ToList();
        var open = await Cases(facilityId).AsNoTracking()
            .Where(d => ids.Contains(d.ReceiptPaymentId) && d.Status != PaymentDisputeStatus.Won && d.Status != PaymentDisputeStatus.Lost)
            .Select(d => d.ReceiptPaymentId)
            .ToListAsync(ct);
        return lines.Select(p => new DisputablePaymentDto(p.Id, p.Mode, p.Amount, p.ExternalId ?? p.Reference, open.Contains(p.Id))).ToList();
    }

    public async Task<Result<PaymentDisputeDto>> DescribeAsync(Guid id, DescribePaymentDisputeRequest request, CancellationToken ct) =>
        await ChangeAsync(id, request.Version, d => d.Describe(request.ReasonCode, request.Reason, request.RespondBy), "billing.dispute.described",
            d => $"Chargeback {d.Number}: reason {d.ReasonCode ?? "—"}, answer by {d.RespondBy?.ToString("dd-MM-yyyy") ?? "—"}", ct);

    public async Task<Result<PaymentDisputeDto>> RecordEvidenceAsync(Guid id, PaymentDisputeEvidenceRequest request, CancellationToken ct) =>
        await ChangeAsync(id, request.Version, d => d.RecordEvidence(request.SentOn, request.Note, UserName(), BillingTime.Today(time)), "billing.dispute.evidence_sent",
            d => $"Chargeback {d.Number}: evidence sent on {d.EvidenceSentOn:dd-MM-yyyy}: {d.EvidenceNote}", ct);

    public async Task<Result<PaymentDisputeDto>> WinAsync(Guid id, PaymentDisputeNoteRequest request, CancellationToken ct)
    {
        var dispute = await Cases(null).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dispute is not null && await LossWaitingAsync(dispute, ct))
        {
            return Error.Rule("billing.dispute_loss_waiting",
                $"Accepting the loss of chargeback {dispute.Number} waits for a second person: it is rejected, withdrawn or expires before the case is recorded as won.");
        }

        return await ChangeAsync(id, request.Version, d => d.Win(request.Note, UserName(), time.GetUtcNow()), "billing.dispute.won", d => $"Chargeback {d.Number} won: {d.Outcome}", ct);
    }

    /// <summary>
    /// Asks for the loss to be accepted: the receipt's payment is asked to be reversed as taken back by a chargeback. A
    /// second person who approves reversals makes it; the case is lost when they do. The whole payment must have been
    /// taken back: part of one is put right on the bill instead.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> AcceptLossAsync(Guid id, PaymentDisputeNoteRequest request, CancellationToken ct)
    {
        var dispute = await Cases(null).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dispute is null)
        {
            return Error.NotFound(nameof(PaymentDispute), id);
        }

        db.SetExpectedVersion(dispute, request.Version);
        if (dispute.IsClosed)
        {
            return dispute.LossAsked(null, string.Empty, Guid.Empty, time.GetUtcNow()).Error!;
        }

        var payment = await db.Set<ReceiptPayment>().AsNoTracking().FirstAsync(p => p.Id == dispute.ReceiptPaymentId, ct);
        if (dispute.Amount != payment.Amount)
        {
            return Error.Rule("billing.dispute_part",
                $"Chargeback {dispute.Number} took back ₹{Money.FormatInr(dispute.Amount)} of a ₹{Money.FormatInr(payment.Amount)} payment. A receipt's payment is reversed whole: put part of one right on the bill (a credit note or a new receipt) instead.");
        }

        // The reversal asked for and the case that waits for it are saved together: a case never waits on a request it
        // does not know of, so it cannot be recorded as won while its loss is about to be approved.
        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var asked = await reversals.RequestReceiptReversalAsync(
            dispute.ReceiptId, new ReverseReceiptRequest([dispute.ReceiptPaymentId], ReversalReason.Chargeback, $"Chargeback {dispute.Number}: {request.Note.Trim()}"), ct);
        if (asked.IsFailure)
        {
            return asked.Error!;
        }

        dispute.LossAsked(UserName(), asked.Value.SubjectType, asked.Value.SubjectId, time.GetUtcNow());
        Audit(dispute, "billing.dispute.loss_asked", $"Chargeback {dispute.Number}: accepting the loss of ₹{Money.FormatInr(dispute.Amount)} asked for: {request.Note.Trim()}");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return asked.Value;
    }

    public async Task<Result<PaymentDisputeDto>> GetAsync(Guid id, CancellationToken ct) =>
        await Cases(null).AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) is { } dispute
            ? (await ToDtosAsync([dispute], ct))[0]
            : Error.NotFound(nameof(PaymentDispute), id);

    /// <summary>
    /// The facility's cases: <paramref name="open"/> true for those not yet won or lost, false for those closed;
    /// <paramref name="terminal"/> true for card and UPI terminal payments, false for the gateway's. Open cases come by
    /// their deadline (soonest first, those without one last), closed ones newest first.
    /// </summary>
    public async Task<PagedResult<PaymentDisputeDto>> ListAsync(Guid facilityId, bool? open, bool? terminal, int page, int pageSize, CancellationToken ct)
    {
        var cases = Cases(facilityId).AsNoTracking();
        if (open is { } isOpen)
        {
            cases = isOpen
                ? cases.Where(d => d.Status == PaymentDisputeStatus.Open || d.Status == PaymentDisputeStatus.Contested)
                : cases.Where(d => d.Status == PaymentDisputeStatus.Won || d.Status == PaymentDisputeStatus.Lost);
        }

        if (terminal is { } isTerminal)
        {
            cases = isTerminal ? cases.Where(d => d.Mode != PaymentMode.PaymentGateway) : cases.Where(d => d.Mode == PaymentMode.PaymentGateway);
        }

        var total = await cases.CountAsync(ct);
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var number = Math.Max(page, 1);
        var items = await cases
            .OrderBy(d => d.Status == PaymentDisputeStatus.Won || d.Status == PaymentDisputeStatus.Lost)
            .ThenBy(d => d.RespondBy == null)
            .ThenBy(d => d.RespondBy)
            .ThenByDescending(d => d.ClosedAt)
            .ThenByDescending(d => d.OpenedAt)
            .Skip((number - 1) * size)
            .Take(size)
            .ToListAsync(ct);
        return new PagedResult<PaymentDisputeDto>(await ToDtosAsync(items, ct), number, size, total);
    }

    public async Task<PaymentDisputeSummaryDto> SummaryAsync(Guid facilityId, CancellationToken ct)
    {
        var today = BillingTime.Today(time);
        var open = await Cases(facilityId).AsNoTracking()
            .Where(d => d.Status == PaymentDisputeStatus.Open || d.Status == PaymentDisputeStatus.Contested)
            .Select(d => new { d.Status, d.Amount, d.RespondBy })
            .ToListAsync(ct);
        var unanswered = open.Where(d => d.Status == PaymentDisputeStatus.Open && d.RespondBy is not null).ToList();
        return new PaymentDisputeSummaryDto(
            open.Count, open.Sum(d => d.Amount),
            unanswered.Count(d => d.RespondBy >= today && d.RespondBy <= today.AddDays(PaymentDispute.RemindDays)),
            unanswered.Count(d => d.RespondBy < today));
    }

    /// <summary>
    /// Tells those who reconcile payments of each case HC360 opened from a settlement (once it is saved), and reminds
    /// them, once a day, of every case not yet answered whose deadline is within <see cref="PaymentDispute.RemindDays"/>
    /// days or past. Each case's <see cref="PaymentDisputeReminder"/> is claimed before anyone is told, so a second server
    /// running the same step, or this step again after a failure, never tells the same thing twice; the case itself is not
    /// changed, so an accountant who has it open is not told it changed. Returns how many it told.
    /// </summary>
    public async Task<int> RemindAsync(CancellationToken ct)
    {
        var told = 0;
        var opened = await (
                from r in db.Set<PaymentDisputeReminder>()
                join d in AllCases() on r.DisputeId equals d.Id
                where r.AnnouncedAt == null
                orderby d.OpenedAt
                select d)
            .AsNoTracking()
            .Take(200)
            .ToListAsync(ct);
        foreach (var dispute in opened)
        {
            var now = time.GetUtcNow();
            if (await db.Set<PaymentDisputeReminder>().Where(r => r.DisputeId == dispute.Id && r.AnnouncedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.AnnouncedAt, now), ct) == 0)
            {
                continue;
            }

            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.GatewayReconcile, dispute.FacilityId, $"Chargeback {dispute.Number}: ₹{Money.FormatInr(dispute.Amount)} taken back",
                $"The patient's bank took back ₹{Money.FormatInr(dispute.Amount)} of receipt {dispute.ReceiptNumber}. Record what the bank's notice says and the date to answer by.",
                Link(dispute), "billing", "warning", ct);
            told++;
        }

        // A deadline is reminded once a day, and again the same day when it changed since (PaymentDisputeReminder.IsDue).
        var today = BillingTime.Today(time);
        var soon = today.AddDays(PaymentDispute.RemindDays);
        var due = await (
                from d in AllCases()
                join r in db.Set<PaymentDisputeReminder>() on d.Id equals r.DisputeId
                where d.Status == PaymentDisputeStatus.Open && d.RespondBy != null && d.RespondBy <= soon
                      && (r.RemindedOn != today || r.RemindedFor != d.RespondBy)
                orderby d.RespondBy
                select d)
            .AsNoTracking()
            .Take(200)
            .ToListAsync(ct);
        foreach (var dispute in due)
        {
            var respondBy = dispute.RespondBy!.Value;
            if (await db.Set<PaymentDisputeReminder>()
                    .Where(r => r.DisputeId == dispute.Id && (r.RemindedOn != today || r.RemindedFor != respondBy))
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.RemindedOn, today).SetProperty(r => r.RemindedFor, respondBy), ct) == 0)
            {
                continue;
            }

            var late = respondBy < today;
            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.GatewayReconcile, dispute.FacilityId,
                late ? $"Chargeback {dispute.Number} is past its deadline" : $"Chargeback {dispute.Number}: answer by {respondBy:dd MMM yyyy}",
                $"₹{Money.FormatInr(dispute.Amount)} of receipt {dispute.ReceiptNumber}. Send the evidence to the bank or the gateway and record it, or accept the loss.",
                Link(dispute), "billing", late ? "error" : "warning", ct);
            told++;
        }

        return told;
    }

    /// <summary>The reversal a second person approved on a case: the case is lost (in the reversal's unit of work).</summary>
    internal async Task LoseAsync(ReceiptReversal reversal, CancellationToken ct)
    {
        var payments = reversal.Lines.Select(l => l.ReceiptPaymentId).ToList();
        var cases = await AllCases()
            .Where(d => payments.Contains(d.ReceiptPaymentId) && (d.Status == PaymentDisputeStatus.Open || d.Status == PaymentDisputeStatus.Contested))
            .ToListAsync(ct);
        foreach (var dispute in cases.Where(d => d.Lose(reversal.Id, reversal.Number, reversal.ReversedAt)))
        {
            Audit(dispute, "billing.dispute.lost", $"Chargeback {dispute.Number} lost: receipt {dispute.ReceiptNumber} reversed ({reversal.Number})");
        }
    }

    private async Task<Result<PaymentDisputeDto>> ChangeAsync(
        Guid id, uint version, Func<PaymentDispute, Result> change, string action, Func<PaymentDispute, string> describe, CancellationToken ct)
    {
        var dispute = await Cases(null).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dispute is null)
        {
            return Error.NotFound(nameof(PaymentDispute), id);
        }

        db.SetExpectedVersion(dispute, version);
        var changed = change(dispute);
        if (changed.IsFailure)
        {
            return changed.Error!;
        }

        Audit(dispute, action, describe(dispute));
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([dispute], ct))[0];
    }

    private async Task<PaymentDispute> OpenAsync(
        Receipt receipt, ReceiptPayment payment, PaymentDisputeSource source, decimal amount, string? reference, string? openedBy, Action<PaymentDispute> details, CancellationToken ct)
    {
        var dispute = new PaymentDispute
        {
            FacilityId = receipt.FacilityId,
            Number = await numbers.NextAsync(BillingNumberSeries.PaymentDispute, receipt.FacilityId, ct),
            Source = source,
            Status = PaymentDisputeStatus.Open,
            ReceiptId = receipt.Id,
            ReceiptPaymentId = payment.Id,
            ReceiptNumber = receipt.ReceiptNumber,
            BillId = receipt.BillId,
            PatientId = receipt.PatientId,
            Mode = payment.Mode,
            PaymentReference = reference is { Length: > 64 } ? reference[..64] : reference,
            Amount = Money.Round(amount),
            OpenedAt = time.GetUtcNow(),
            OpenedByName = openedBy,
        };
        details(dispute);
        db.Add(dispute);
        db.Add(new PaymentDisputeReminder { DisputeId = dispute.Id, AnnouncedAt = openedBy is null ? null : dispute.OpenedAt });
        Audit(dispute, "billing.dispute.opened",
            $"Chargeback {dispute.Number} opened{(openedBy is null ? $" from the {(source == PaymentDisputeSource.Gateway ? "gateway's settlement" : "acquirer's report")}" : " from the notice")}: ₹{Money.FormatInr(dispute.Amount)} of receipt {receipt.ReceiptNumber} taken back");
        return dispute;
    }

    /// <summary>The open case an accountant opened from the notice for this payment, now carried by a settlement.</summary>
    private async Task<PaymentDispute?> JoinNoticeAsync(Guid receiptPaymentId, Guid facilityId, Action<PaymentDispute> link, CancellationToken ct)
    {
        var notice = await Cases(facilityId).FirstOrDefaultAsync(
            d => d.ReceiptPaymentId == receiptPaymentId && d.Source == PaymentDisputeSource.Notice && d.GatewaySettlementLineId == null && d.PosSettlementLineId == null
                 && d.Status != PaymentDisputeStatus.Won && d.Status != PaymentDisputeStatus.Lost, ct);
        if (notice is not null)
        {
            link(notice);
        }

        return notice;
    }

    private async Task<(Receipt Receipt, ReceiptPayment Payment)?> PaymentAsync(Guid facilityId, System.Linq.Expressions.Expression<Func<ReceiptPayment, bool>> which, CancellationToken ct)
    {
        var found = await (
                from p in db.Set<ReceiptPayment>().Where(which)
                join r in db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.ReceiptId equals r.Id
                where r.FacilityId == facilityId
                select new { Payment = p, Receipt = r })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        return found is null ? null : (found.Receipt, found.Payment);
    }

    /// <summary>The cases of <paramref name="facilityId"/>, or of the signed-in facility when null (the facility filter applies).</summary>
    private IQueryable<PaymentDispute> Cases(Guid? facilityId) =>
        facilityId is { } id
            ? db.Set<PaymentDispute>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(d => d.FacilityId == id)
            : db.Set<PaymentDispute>();

    /// <summary>Every facility's cases (the reconciliation job, and a reversal approved for any facility).</summary>
    private IQueryable<PaymentDispute> AllCases() => db.Set<PaymentDispute>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);

    private async Task<bool> LossWaitingAsync(PaymentDispute dispute, CancellationToken ct) =>
        dispute.LossSubjectType is { } type && dispute.LossSubjectId is { } subject
        && (await approvals.WaitingSinceAsync(ReceiptReversalApproval.Key, type, [subject], ct)).ContainsKey(subject);

    private async Task<List<PaymentDisputeDto>> ToDtosAsync(IReadOnlyList<PaymentDispute> disputes, CancellationToken ct)
    {
        var waiting = new HashSet<Guid>();
        foreach (var group in disputes.Where(d => !d.IsClosed && d.LossSubjectType is not null && d.LossSubjectId is not null).GroupBy(d => d.LossSubjectType!))
        {
            var since = await approvals.WaitingSinceAsync(ReceiptReversalApproval.Key, group.Key, group.Select(d => d.LossSubjectId!.Value).Distinct().ToList(), ct);
            waiting.UnionWith(group.Where(d => since.ContainsKey(d.LossSubjectId!.Value)).Select(d => d.Id));
        }

        var today = BillingTime.Today(time);
        return disputes.Select(d => new PaymentDisputeDto(
            d.Id, d.Number, d.Source, d.Status, d.ReceiptId, d.ReceiptNumber, d.ReceiptPaymentId, d.BillId, d.Mode, d.PaymentReference, d.Amount,
            d.ReasonCode, d.Reason, d.RespondBy,
            d.Status == PaymentDisputeStatus.Open && d.RespondBy is { } by ? by.DayNumber - today.DayNumber : null,
            d.OpenedAt, d.OpenedByName, d.EvidenceSentOn, d.EvidenceNote, d.EvidenceByName, d.LossAskedAt, d.LossAskedByName, waiting.Contains(d.Id),
            d.ClosedAt, d.ClosedByName, d.Outcome, d.ReversalNumber, d.Version)).ToList();
    }

    private void Audit(PaymentDispute dispute, string action, string message) =>
        audit.Add(new AuditEntry(AuditCategories.Data, action, message)
        {
            EntityType = nameof(PaymentDispute),
            EntityId = dispute.Id.ToString(),
            PatientId = dispute.PatientId,
            Severity = AuditSeverity.Warning,
        });

    private string? UserName() => currentUser.DisplayName ?? currentUser.UserName;

    private static string Link(PaymentDispute dispute) =>
        dispute.Mode == PaymentMode.PaymentGateway ? "/billing/reconciliation?view=chargebacks" : "/billing/card-settlements?view=chargebacks";
}

/// <summary>A receipt reversal for a chargeback, approved and made: the case it was asked from is lost.</summary>
internal sealed class PaymentReversedDisputeHandler(HospitalDbContext db, PaymentDisputeService disputes) : IDomainEventHandler<PaymentReversed>
{
    public async Task HandleAsync(PaymentReversed reversed, CancellationToken cancellationToken)
    {
        var reversal = db.Set<ReceiptReversal>().Local.FirstOrDefault(r => r.Id == reversed.ReversalId)
                       ?? await db.Set<ReceiptReversal>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(r => r.Lines)
                           .FirstOrDefaultAsync(r => r.Id == reversed.ReversalId, cancellationToken);
        if (reversal is { Reason: ReversalReason.Chargeback })
        {
            await disputes.LoseAsync(reversal, cancellationToken);
        }
    }
}
