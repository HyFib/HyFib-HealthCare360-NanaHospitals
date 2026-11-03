using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Chargebacks (<see cref="PaymentDispute"/>): a case is opened when a gateway settlement takes money back on a payment
/// HC360 recorded, when the acquirer's report carries a chargeback on a terminal payment HC360 matched, or by an
/// accountant from the bank's notice. A gateway payment's case follows the gateway's own record of the dispute (read
/// from the gateway when its webhook arrives, and hourly): its reason code, phase, status and deadline. Accountants
/// record what the notice says, the evidence sent and a win; accepting the loss asks for the receipt to be reversed,
/// which a second person who approves reversals makes. Those holding <see cref="BillingPermissions.GatewayReconcile"/>
/// are told of each new case and of a loss the gateway decided, and reminded daily of a deadline near or past. Every
/// change is audited.
/// </summary>
public sealed class PaymentDisputeService(
    HospitalDbContext db,
    INumberSeries numbers,
    IApprovals approvals,
    ReversalService reversals,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IServiceProvider services,
    TimeProvider time,
    ILogger<PaymentDisputeService> logger)
{
    public const int MaxPageSize = 100;

    /// <summary>The hourly read of the gateway's disputes covers those raised in this many days.</summary>
    public const int LookBackDays = 30;

    /// <summary>The modes a bank can take money back from through a chargeback.</summary>
    private static readonly PaymentMode[] DisputableModes = [PaymentMode.Card, PaymentMode.Upi, PaymentMode.PaymentGateway, PaymentMode.NetBanking, PaymentMode.Wallet];

    /// <summary>
    /// A gateway settlement's adjustment on a payment HC360 recorded: money taken back opens a case (or joins the case of
    /// the dispute the gateway names on it, or the open case on the payment that no settlement carried yet); the money
    /// taken coming back wins the open case, or is the money back of a case already won. Returns why the line needs no
    /// exception, or null when it does (a payment HC360 does not know, a second debit or return for one case, money
    /// returned on a lost case, part of what was taken given back (the case stays open for the rest), or money added
    /// that no case explains).
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

        // The gateway names the dispute an adjustment was made for: that dispute's case, whoever opened it first.
        var ofDispute = line.DisputeId is { } disputeId ? await AllCases().FirstOrDefaultAsync(d => d.GatewayDisputeId == disputeId, ct) : null;
        if (line.Gross > 0)
        {
            // The settlement is read again on every fetch of its day: a return already followed is followed.
            if (await Cases(line.FacilityId).FirstOrDefaultAsync(d => d.ReturnLineId == line.Id, ct) is { } followed)
            {
                return $"The gateway returned the money of chargeback {followed.Number}.";
            }

            var dispute = ofDispute
                          ?? await Cases(line.FacilityId).FirstOrDefaultAsync(d => d.ReceiptPaymentId == payment.Value.Payment.Id && d.Status != PaymentDisputeStatus.Won && d.Status != PaymentDisputeStatus.Lost, ct);
            // Only the money taken coming back wins the case by itself. Part of it (or more) does not: the case stays open
            // for what is still taken back, and the return is an exception for a person to put against it.
            if (dispute is null || line.Gross != dispute.Amount)
            {
                return null;
            }

            var returned = $"₹{Money.FormatInr(line.Gross)}";
            if (dispute.WonByReturn($"The gateway returned {returned} in settlement {settlementId} ({line.EntityId}).", line.Id, time.GetUtcNow()))
            {
                Audit(dispute, "billing.dispute.won", $"Chargeback {dispute.Number} won: the gateway returned {returned} in settlement {settlementId}");
            }
            else if (dispute.Returned(line.Id))
            {
                Audit(dispute, "billing.dispute.returned", $"Chargeback {dispute.Number}, won: the gateway returned {returned} in settlement {settlementId}");
            }
            else
            {
                return null;
            }

            return $"The gateway returned the money of chargeback {dispute.Number}.";
        }

        var taken = await Cases(line.FacilityId).FirstOrDefaultAsync(d => d.GatewaySettlementLineId == line.Id, ct);
        if (taken is null && ofDispute is not null)
        {
            if (ofDispute.GatewaySettlementLineId is not null)
            {
                return null;
            }

            ofDispute.GatewaySettlementLineId = line.Id;
            taken = ofDispute;
        }

        taken ??= await JoinOpenCaseAsync(payment.Value.Payment.Id, line.FacilityId, line.DisputeId, d => d.GatewaySettlementLineId = line.Id, ct)
                  ?? await OpenAsync(payment.Value.Receipt, payment.Value.Payment, PaymentDisputeSource.Gateway, -line.Gross, paymentId, null,
                      d =>
                      {
                          d.GatewaySettlementLineId = line.Id;
                          d.GatewayDisputeId = line.DisputeId;
                      }, ct);
        return $"Followed as chargeback {taken.Number}.";
    }

    /// <summary>
    /// Brings HC360 in line with a dispute as the gateway holds it: read from the gateway with the facility's account,
    /// never taken from a webhook's body. The case following that dispute, else the open case on the payment that no
    /// other dispute has (opened from the notice, or from the settlement that took the money), else a new
    /// <see cref="PaymentDisputeSource.Gateway"/> case, takes the gateway's facts (<see cref="PaymentDispute.FollowGateway"/>).
    /// A dispute on a payment HC360 never recorded opens nothing (no case). Returns the case and what was done, in words;
    /// the caller saves.
    /// </summary>
    internal async Task<(PaymentDispute? Case, string Note)> FollowGatewayDisputeAsync(GatewayDispute gatewayDispute, CancellationToken ct)
    {
        var dispute = await AllCases().FirstOrDefaultAsync(d => d.GatewayDisputeId == gatewayDispute.DisputeId, ct);
        var opened = false;
        if (dispute is null)
        {
            var payment = await PaymentAsync(null, p => p.Mode == PaymentMode.PaymentGateway && p.ExternalId == gatewayDispute.PaymentId, ct);
            if (payment is null)
            {
                return (null, $"Dispute {gatewayDispute.DisputeId} is on payment {gatewayDispute.PaymentId}, which HC360 did not record: nothing to follow.");
            }

            var (receipt, line) = payment.Value;
            dispute = await JoinOpenCaseAsync(line.Id, receipt.FacilityId, gatewayDispute.DisputeId, _ => { }, ct, anyLine: true);
            if (dispute is null)
            {
                dispute = await OpenAsync(receipt, line, PaymentDisputeSource.Gateway, Math.Min(gatewayDispute.Amount, line.Amount), gatewayDispute.PaymentId, null,
                    d => d.GatewayDisputeId = gatewayDispute.DisputeId, ct);
                opened = true;
            }
        }

        var changed = dispute.FollowGateway(
            gatewayDispute, LastDay(gatewayDispute.RespondBy),
            gatewayDispute.EvidenceSubmittedAt is { } submitted ? BillingTime.LocalDate(submitted) : null, time.GetUtcNow());
        if (changed is null)
        {
            return (dispute, $"Chargeback {dispute.Number} is as the gateway's dispute {gatewayDispute.DisputeId} says.");
        }

        Audit(dispute, dispute.Status == PaymentDisputeStatus.Won ? "billing.dispute.won" : "billing.dispute.followed",
            $"Chargeback {dispute.Number}, the gateway's dispute {gatewayDispute.DisputeId}: {changed}");
        return (dispute, opened ? $"Chargeback {dispute.Number} opened from the gateway's dispute {gatewayDispute.DisputeId}." : $"Chargeback {dispute.Number}: {changed}.");
    }

    /// <summary>
    /// A dispute's webhook: the dispute is read from the gateway with the account of <paramref name="facilityId"/> (the
    /// webhook's body is not trusted beyond its id) and followed. Failed when it could not be read (the gateway delivers
    /// the event again, and the hourly read catches it); ignored when it is on a payment HC360 did not record.
    /// </summary>
    internal async Task<(WebhookEventOutcome Outcome, string Note)> ReadGatewayDisputeAsync(string disputeId, Guid? facilityId, CancellationToken ct)
    {
        if (services.GetService<IPaymentGateway>() is not { } gateway)
        {
            return (WebhookEventOutcome.Ignored, "No payment gateway is set up.");
        }

        var read = await gateway.GetDisputeAsync(disputeId, facilityId, ct);
        if (!read.Available || read.Disputes is not [{ } dispute] || dispute.DisputeId != disputeId)
        {
            return (WebhookEventOutcome.Failed, read.Problem ?? $"The gateway did not give dispute {disputeId}.");
        }

        var (followed, note) = await FollowGatewayDisputeAsync(dispute, ct);
        await db.SaveChangesAsync(ct);
        return (followed is null ? WebhookEventOutcome.Ignored : WebhookEventOutcome.Processed, note);
    }

    /// <summary>
    /// The hourly read of the facility's gateway: every dispute raised in the last <see cref="LookBackDays"/> days, then
    /// each open case following a dispute raised before that, read again by its id (at most 50 a run). Webhooks bring a
    /// dispute within seconds; this catches one the gateway could not deliver, and one whose webhook HC360 could not
    /// handle. A case the webhook opened at the same moment clashes on the dispute's unique index and is read on the next
    /// run. Returns how many disputes were read, or null when the gateway's disputes could not be read.
    /// </summary>
    public async Task<int?> FollowGatewayDisputesAsync(Guid facilityId, CancellationToken ct)
    {
        if (services.GetService<IPaymentGateway>() is not { } gateway)
        {
            return null;
        }

        var listed = await gateway.GetDisputesAsync(time.GetUtcNow().AddDays(-LookBackDays), facilityId, ct);
        if (!listed.Available)
        {
            logger.LogDebug("The gateway's disputes were not read for facility {FacilityId}: {Reason}", facilityId, listed.Problem);
            return null;
        }

        var read = 0;
        foreach (var dispute in listed.Disputes)
        {
            read += await FollowAndSaveAsync(dispute, ct) ? 1 : 0;
        }

        var seen = listed.Disputes.Select(d => d.DisputeId).ToHashSet(StringComparer.Ordinal);
        var older = (await Cases(facilityId).AsNoTracking()
                .Where(d => d.GatewayDisputeId != null && d.Status != PaymentDisputeStatus.Won && d.Status != PaymentDisputeStatus.Lost)
                .OrderBy(d => d.OpenedAt)
                .Select(d => d.GatewayDisputeId!)
                .ToListAsync(ct))
            .Where(id => !seen.Contains(id))
            .Take(50)
            .ToList();
        foreach (var id in older)
        {
            var one = await gateway.GetDisputeAsync(id, facilityId, ct);
            if (one.Available && one.Disputes is [{ } dispute] && dispute.DisputeId == id)
            {
                read += await FollowAndSaveAsync(dispute, ct) ? 1 : 0;
            }
        }

        return read;
    }

    private async Task<bool> FollowAndSaveAsync(GatewayDispute dispute, CancellationToken ct)
    {
        try
        {
            await FollowGatewayDisputeAsync(dispute, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogInformation(ex, "Dispute {DisputeId} was followed at the same moment by another read; the next run reads it again", dispute.DisputeId);
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// The last day to answer: the Indian calendar day before the gateway's deadline instant (Razorpay's
    /// <c>respond_by</c> is the midnight that ends that day).
    /// </summary>
    internal static DateOnly? LastDay(DateTimeOffset? respondBy) => respondBy is { } by ? BillingTime.LocalDate(by.AddTicks(-1)) : null;

    /// <summary>
    /// A chargeback in the acquirer's report on a terminal payment HC360 matched (the sale's transaction with the same
    /// RRN, or approval code at the same terminal, names the receipt): it opens a case, or joins the one opened from the
    /// notice. A chargeback on a payment HC360 did not match stays to look at.
    /// </summary>
    internal async Task TakeTerminalChargebacksAsync(IReadOnlyList<PosSettlementLine> lines, CancellationToken ct)
    {
        // Chargebacks first: a report can carry a chargeback and its reversal, and the reversal wins the case.
        foreach (var line in lines.Where(l => l.Type == PosSettlementLineType.Adjustment && l.DisputeId is null && l.ReviewedAt is null && l.WithdrawnOn is null)
                     .OrderBy(l => l.GivesBack))
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

            if (line.GivesBack)
            {
                // The acquirer reversed the chargeback: the money is back, and the case on that sale is won by itself.
                var won = await Cases(line.FacilityId).FirstOrDefaultAsync(d => d.ReturnLineId == line.Id, ct)
                          ?? await Cases(line.FacilityId).Where(d => d.ReceiptPaymentId == receiptPaymentId && d.Status != PaymentDisputeStatus.Lost)
                              .OrderBy(d => d.Status == PaymentDisputeStatus.Won)
                              .FirstOrDefaultAsync(ct);
                // Only the whole amount back wins the case by itself: part of it stays to look at, and the case open.
                if (won is null || (won.ReturnLineId != line.Id && line.Amount != won.Amount))
                {
                    continue;
                }

                var outcome = $"The acquirer reversed the chargeback: ₹{Money.FormatInr(line.Amount)} back in the payout of {line.SettledOn:dd-MM-yyyy}.";
                if (won.ReturnLineId == line.Id)
                {
                    line.DisputeId = won.Id;
                }
                else if (won.WonByReturn(outcome, line.Id, time.GetUtcNow()))
                {
                    Audit(won, "billing.dispute.won", $"Chargeback {won.Number} won: {outcome}");
                    line.DisputeId = won.Id;
                }
                else if (won.Returned(line.Id))
                {
                    Audit(won, "billing.dispute.returned", $"Chargeback {won.Number}, won: {outcome}");
                    line.DisputeId = won.Id;
                }

                continue;
            }

            var dispute = await Cases(line.FacilityId).FirstOrDefaultAsync(d => d.PosSettlementLineId == line.Id, ct)
                          ?? await JoinOpenCaseAsync(receiptPaymentId, line.FacilityId, null, d => d.PosSettlementLineId = line.Id, ct)
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
            .Select(d => new { d.Status, d.Amount, d.RespondBy, d.GatewayStatus })
            .ToListAsync(ct);
        var unanswered = open.Where(d => d.Status == PaymentDisputeStatus.Open && d.RespondBy is not null && d.GatewayStatus != PaymentDispute.GatewayLost).ToList();
        return new PaymentDisputeSummaryDto(
            open.Count, open.Sum(d => d.Amount),
            unanswered.Count(d => d.RespondBy >= today && d.RespondBy <= today.AddDays(PaymentDispute.RemindDays)),
            unanswered.Count(d => d.RespondBy < today),
            open.Count(d => d.GatewayStatus == PaymentDispute.GatewayLost));
    }

    /// <summary>
    /// Tells those who reconcile payments of each open case HC360 opened itself (from a settlement or the gateway's
    /// dispute, once it is saved) and of each dispute the gateway decided against the hospital, and reminds them, once a
    /// day, of every case not yet answered whose deadline is within <see cref="PaymentDispute.RemindDays"/> days or past.
    /// Each case's <see cref="PaymentDisputeReminder"/> is claimed before anyone is told, so a second server running the
    /// same step, or this step again after a failure, never tells the same thing twice; the case itself is not changed, so
    /// an accountant who has it open is not told it changed. Returns how many it told.
    /// </summary>
    public async Task<int> RemindAsync(CancellationToken ct)
    {
        var told = 0;
        var opened = await (
                from r in db.Set<PaymentDisputeReminder>()
                join d in AllCases() on r.DisputeId equals d.Id
                where r.AnnouncedAt == null && d.Status != PaymentDisputeStatus.Won && d.Status != PaymentDisputeStatus.Lost
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

            await (dispute.GatewayDisputeId is { } gatewayId
                ? notifications.NotifyPermissionHoldersAsync(
                    BillingPermissions.GatewayReconcile, dispute.FacilityId, $"Chargeback {dispute.Number}: ₹{Money.FormatInr(dispute.Amount)} disputed",
                    $"The patient's bank disputes ₹{Money.FormatInr(dispute.Amount)} of receipt {dispute.ReceiptNumber} (the gateway's dispute {gatewayId}"
                    + $"{(dispute.RespondBy is { } by ? $", to answer by {by:dd MMM yyyy}" : string.Empty)}). Send the evidence at the gateway and record it, or accept the loss.",
                    Link(dispute), "billing", "warning", ct)
                : notifications.NotifyPermissionHoldersAsync(
                    BillingPermissions.GatewayReconcile, dispute.FacilityId, $"Chargeback {dispute.Number}: ₹{Money.FormatInr(dispute.Amount)} taken back",
                    $"The patient's bank took back ₹{Money.FormatInr(dispute.Amount)} of receipt {dispute.ReceiptNumber}. Record what the bank's notice says and the date to answer by.",
                    Link(dispute), "billing", "warning", ct));
            told++;
        }

        // A loss the gateway decided is told once: the next step is to accept it, which a second person approves.
        var lost = await (
                from d in AllCases()
                join r in db.Set<PaymentDisputeReminder>() on d.Id equals r.DisputeId
                where d.GatewayStatus == PaymentDispute.GatewayLost && d.Status != PaymentDisputeStatus.Won && d.Status != PaymentDisputeStatus.Lost
                      && r.LostToldAt == null
                orderby d.OpenedAt
                select d)
            .AsNoTracking()
            .Take(200)
            .ToListAsync(ct);
        foreach (var dispute in lost)
        {
            var now = time.GetUtcNow();
            if (await db.Set<PaymentDisputeReminder>().Where(r => r.DisputeId == dispute.Id && r.LostToldAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.LostToldAt, now), ct) == 0)
            {
                continue;
            }

            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.GatewayReconcile, dispute.FacilityId, $"Chargeback {dispute.Number} was lost at the gateway",
                $"The bank decided the gateway's dispute {dispute.GatewayDisputeId} against the hospital: ₹{Money.FormatInr(dispute.Amount)} of receipt {dispute.ReceiptNumber} is gone. Accept the loss so the bill owes it again; a second person approves the reversal.",
                Link(dispute), "billing", "error", ct);
            told++;
        }

        // A deadline is reminded once a day, and again the same day when it changed since (PaymentDisputeReminder.IsDue).
        var today = BillingTime.Today(time);
        var soon = today.AddDays(PaymentDispute.RemindDays);
        var due = await (
                from d in AllCases()
                join r in db.Set<PaymentDisputeReminder>() on d.Id equals r.DisputeId
                where d.Status == PaymentDisputeStatus.Open && d.RespondBy != null && d.RespondBy <= soon
                      && (d.GatewayStatus == null || d.GatewayStatus != PaymentDispute.GatewayLost)
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

    /// <summary>
    /// The open case on this payment that no settlement has carried yet (opened from the notice, or from the gateway's
    /// dispute) and that follows no other gateway dispute than <paramref name="disputeId"/>: a settlement, or the gateway's
    /// dispute, joins it rather than opening a second case. <paramref name="anyLine"/>: a case a settlement already
    /// carries is joined too (the gateway's dispute joining the case its debit opened).
    /// </summary>
    private async Task<PaymentDispute?> JoinOpenCaseAsync(
        Guid receiptPaymentId, Guid facilityId, string? disputeId, Action<PaymentDispute> link, CancellationToken ct, bool anyLine = false)
    {
        var open = await Cases(facilityId)
            .Where(d => d.ReceiptPaymentId == receiptPaymentId && d.Status != PaymentDisputeStatus.Won && d.Status != PaymentDisputeStatus.Lost
                        && (anyLine || (d.GatewaySettlementLineId == null && d.PosSettlementLineId == null))
                        && (d.GatewayDisputeId == null || d.GatewayDisputeId == disputeId))
            .OrderBy(d => d.OpenedAt)
            .FirstOrDefaultAsync(ct);
        if (open is not null)
        {
            link(open);
            if (disputeId is not null)
            {
                open.GatewayDisputeId = disputeId;
            }
        }

        return open;
    }

    /// <summary>A receipt's payment line and its receipt, in <paramref name="facilityId"/> (null: whichever facility took it).</summary>
    private async Task<(Receipt Receipt, ReceiptPayment Payment)?> PaymentAsync(Guid? facilityId, System.Linq.Expressions.Expression<Func<ReceiptPayment, bool>> which, CancellationToken ct)
    {
        var found = await (
                from p in db.Set<ReceiptPayment>().Where(which)
                join r in db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.ReceiptId equals r.Id
                where facilityId == null || r.FacilityId == facilityId
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
            d.Status == PaymentDisputeStatus.Open && !d.DecidedAgainst && d.RespondBy is { } by ? by.DayNumber - today.DayNumber : null,
            d.OpenedAt, d.OpenedByName, d.EvidenceSentOn, d.EvidenceNote, d.EvidenceByName, d.LossAskedAt, d.LossAskedByName, waiting.Contains(d.Id),
            d.ClosedAt, d.ClosedByName, d.Outcome, d.ReversalNumber, d.GatewayDisputeId, d.GatewayStatus, d.GatewayPhase, d.DecidedAgainst,
            d.Version)).ToList();
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
