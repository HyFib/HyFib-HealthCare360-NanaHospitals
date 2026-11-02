using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>One step a receipt reversal takes: <paramref name="Amount"/> taken back from payment line <paramref name="PaymentId"/>.</summary>
public sealed record ReversalStep(Guid PaymentId, decimal Amount);

/// <summary>
/// The receipt reversal asked for, and what the checker was shown it would do: the payment lines of the money, the
/// advance adjustments undone with it, and the refunds closed. It is made only if it would still do exactly that.
/// </summary>
public sealed record ReceiptReversalPayload(
    Guid ReceiptId, IReadOnlyList<ReversalStep> Lines, IReadOnlyList<ReversalStep> Undone, IReadOnlyList<Guid> RefundsClosed, ReversalReason Reason, string Note);

/// <summary>The refund asked to be recorded as not paid out.</summary>
public sealed record RefundReversalPayload(Guid RefundId, ReversalReason Reason, string Note);

/// <summary>Money a receipt recorded that never arrived: asked for by one person, made when another approves it.</summary>
internal sealed class ReceiptReversalApproval(ReversalService reversals) : IApprovalAction
{
    public const string Key = "billing.receipt_reversal";

    public string Kind => Key;

    public string Title => "Money not received";

    public string ApprovePermission => BillingPermissions.RefundsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => reversals.ApplyReceiptReversalAsync(request, ct);
}

/// <summary>A refund recorded as paid out that never reached the patient: asked for by one person, made when another approves it.</summary>
internal sealed class RefundReversalApproval(ReversalService reversals) : IApprovalAction
{
    public const string Key = "billing.refund_reversal";

    public string Kind => Key;

    public string Title => "Refund not paid out";

    public string ApprovePermission => BillingPermissions.RefundsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => reversals.ApplyRefundReversalAsync(request, ct);
}

/// <summary>
/// Money recorded as moved that did not move (MB-04, MB-05): a receipt whose money never arrived, and a refund recorded
/// as paid out that never reached the patient. Receipts and refunds are never edited: a reversal stands beside them,
/// dated the day it is made, and puts the balances right (the bill owes again, the advance holds less, the refund's money
/// is back). The cashier or the billing desk asks for it; an approver, never the same person, makes it by approving.
/// Each is audited, in the Tally day book of its day, and in that day's day-end report.
/// </summary>
public sealed class ReversalService(
    HospitalDbContext db,
    INumberSeries numbers,
    IApprovals approvals,
    GatewayDiscrepancyService discrepancies,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>
    /// Asks for <paramref name="request"/>'s payment lines of a receipt to be reversed as never received. Nothing changes
    /// until an approver approves it (<see cref="ApplyReceiptReversalAsync"/>).
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> RequestReceiptReversalAsync(Guid receiptId, ReverseReceiptRequest request, CancellationToken ct)
    {
        var planned = await PlanAsync(receiptId, request.PaymentIds, tracking: false, ct);
        if (planned.IsFailure)
        {
            return planned.Error!;
        }

        var plan = planned.Value;
        var receipt = plan.Receipt;
        var note = request.Note.Trim();
        var payload = new ReceiptReversalPayload(
            receipt.Id, plan.Lines.Select(l => new ReversalStep(l.Line.Id, l.Amount)).ToList(), plan.Undos.Select(u => new ReversalStep(u.Line.Id, u.Amount)).ToList(),
            plan.RefundsToClose.Select(r => r.Id).ToList(), request.Reason, note);
        // The record whose balance changes is the subject: the bill, the stay's account or the advance. Its page shows the
        // request waiting, and one reversal of it waits at a time.
        var subject = receipt.Purpose switch
        {
            ReceiptPurpose.BillPayment => new ApprovalSubject(nameof(Bill), plan.Bill!.Id, plan.Bill.Version, receipt.FacilityId, receipt.PatientId),
            ReceiptPurpose.Deposit => new ApprovalSubject(nameof(IpAccount), plan.Account!.Id, plan.Account.Version, receipt.FacilityId, receipt.PatientId),
            _ => new ApprovalSubject(nameof(PatientAdvance), plan.Advance!.Id, plan.Advance.Version, receipt.FacilityId, receipt.PatientId),
        };
        return await approvals.RequestAsync(
            ReceiptReversalApproval.Key,
            subject,
            payload,
            $"₹{Money.FormatInr(plan.Amount)} of receipt {receipt.ReceiptNumber} not received",
            Details(plan, request.Reason),
            $"{ReasonWords(request.Reason)}: {note}",
            ct);
    }

    /// <summary>
    /// Makes an approved receipt reversal, in the approval's transaction (the approval saves and commits), only if it
    /// would still do what the checker approved: the same lines, adjustments and refunds. The advance adjustments go
    /// first, then the money; refunds that could no longer be paid are closed.
    /// </summary>
    internal async Task<Result<string>> ApplyReceiptReversalAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ReceiptReversalPayload>(approval.Payload);
        var planned = await PlanAsync(asked.ReceiptId, asked.Lines.Select(l => l.PaymentId).ToList(), tracking: true, ct);
        if (planned.IsFailure)
        {
            return planned.Error!;
        }

        var plan = planned.Value;
        var receipt = plan.Receipt;
        if (!Same(plan.Lines.Select(l => new ReversalStep(l.Line.Id, l.Amount)), asked.Lines)
            || !Same(plan.Undos.Select(u => new ReversalStep(u.Line.Id, u.Amount)), asked.Undone)
            || !plan.RefundsToClose.Select(r => r.Id).ToHashSet().SetEquals(asked.RefundsClosed))
        {
            return Error.Rule("approval.stale",
                $"Receipt {receipt.ReceiptNumber}, or the bills, advances and refunds it reaches, changed after this was asked for. Ask again, against them as they are now.");
        }

        var now = time.GetUtcNow();
        var main = await NewReceiptReversalAsync(receipt, plan.Lines, asked, approval, now, causedBy: null, ct);
        main.AdvanceId = plan.Advance?.Id ?? main.AdvanceId;
        foreach (var undo in plan.Undos)
        {
            var reversed = undo.Bill.ReversePayment(undo.Amount);
            if (reversed.IsFailure)
            {
                return reversed.Error!;
            }

            plan.Account?.ReverseAdjustment(undo.Amount);
            plan.Advance?.ReverseAdjustment(undo.Amount);
            await NewReceiptReversalAsync(undo.Adjustment, [(undo.Line, undo.Amount)], asked, approval, now, main.Id, ct);
        }

        var applied = receipt.Purpose switch
        {
            ReceiptPurpose.BillPayment => plan.Bill!.ReversePayment(plan.Amount),
            ReceiptPurpose.Deposit => plan.Account!.ReverseDeposit(plan.Amount),
            _ => plan.Advance!.Reverse(plan.Amount),
        };
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        // A settled stay whose bills owe again is discharged, not closed, until it is settled again.
        foreach (var account in await AccountsOfAsync(plan, ct))
        {
            account.ReopenSettlement();
        }

        foreach (var refund in plan.RefundsToClose)
        {
            refund.CloseUnpaid(currentUser.UserId, $"Closed by {main.Number}: the money it would return was not received.", now);
        }

        main.AnnouncePaymentReversed();
        foreach (var line in plan.Lines.Where(l => l.Line is { Mode: PaymentMode.PaymentGateway, ExternalId: not null }))
        {
            await discrepancies.ClearAsync(DiscrepancyKind.UnsettledReceipt, line.Line.ExternalId!, $"Receipt {receipt.ReceiptNumber} was reversed ({main.Number}): the money did not arrive.", ct);
        }

        Audit(receipt, main, plan);
        return main.Number;
    }

    /// <summary>
    /// Asks for a paid refund to be recorded as not paid out. Nothing changes until an approver approves it
    /// (<see cref="ApplyRefundReversalAsync"/>).
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> RequestRefundReversalAsync(Guid refundId, ReverseRefundRequest request, CancellationToken ct)
    {
        var refund = await db.Set<Refund>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == refundId, ct);
        if (refund is null)
        {
            return Error.NotFound(nameof(Refund), refundId);
        }

        if (ReversalRules.RefundRefusal(refund) is { } refused)
        {
            return refused;
        }

        var note = request.Note.Trim();
        return await approvals.RequestAsync(
            RefundReversalApproval.Key,
            new ApprovalSubject(nameof(Refund), refund.Id, refund.Version, refund.FacilityId, refund.PatientId),
            new RefundReversalPayload(refund.Id, request.Reason, note),
            $"Refund {refund.RefundNumber} of ₹{Money.FormatInr(refund.Amount)} not paid out",
            [
                new("Refund", refund.RefundNumber),
                new("Paid out", $"₹{Money.FormatInr(refund.Amount)} by {refund.Mode} on {BillingTime.LocalDate(refund.PaidAt!.Value):dd MMM yyyy}"),
                new("Reference", refund.GatewayReference ?? refund.Reference ?? "none"),
                new("Why", ReasonWords(request.Reason)),
                new("What changes", $"₹{Money.FormatInr(refund.Amount)} is back on the {PurposeWords(refund.Purpose)}; a new refund pays the patient"),
            ],
            $"{ReasonWords(request.Reason)}: {note}",
            ct);
    }

    /// <summary>
    /// Records an approved refund as not paid out, in the approval's transaction: on the refund as it was asked for (a
    /// refund the gateway updated since was approved on what it no longer says). The money is back on the bill, the IP
    /// deposit or the patient advance, from which a new refund pays the patient.
    /// </summary>
    internal async Task<Result<string>> ApplyRefundReversalAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<RefundReversalPayload>(approval.Payload);
        var refund = await db.Set<Refund>().FirstOrDefaultAsync(r => r.Id == asked.RefundId, ct);
        if (refund is null)
        {
            return Error.NotFound(nameof(Refund), asked.RefundId);
        }

        if (refund.Version != approval.SubjectVersion)
        {
            return Error.Rule("approval.stale", $"Refund {refund.RefundNumber} changed after this was asked for. Ask again, against the refund as it is now.");
        }

        if (ReversalRules.RefundRefusal(refund) is { } refused)
        {
            return refused;
        }

        Bill? bill = null;
        IpAccount? account = null;
        Result restored;
        switch (refund.Purpose)
        {
            case RefundPurpose.Bill:
                bill = await db.Set<Bill>().FirstAsync(b => b.Id == refund.BillId, ct);
                restored = bill.ReverseRefund(refund.Amount);
                account = bill.IpAccountId is { } billAccount ? await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == billAccount, ct) : null;
                break;

            case RefundPurpose.Advance:
                var advance = await db.Set<PatientAdvance>().FirstAsync(a => a.Id == refund.AdvanceId, ct);
                restored = advance.ReverseRefund(refund.Amount);
                break;

            default:
                account = await db.Set<IpAccount>().FirstAsync(a => a.Id == refund.IpAccountId, ct);
                restored = account.ReverseDepositRefund(refund.Amount);
                break;
        }

        if (restored.IsFailure)
        {
            return restored.Error!;
        }

        account?.ReopenSettlement();
        var now = time.GetUtcNow();
        refund.PayoutReversedAt = now;
        var reversal = new RefundReversal
        {
            FacilityId = refund.FacilityId,
            Number = await numbers.NextAsync(BillingNumberSeries.RefundReversal, refund.FacilityId, ct),
            RefundId = refund.Id,
            RefundNumber = refund.RefundNumber,
            PatientId = refund.PatientId,
            Purpose = refund.Purpose,
            BillId = refund.BillId,
            IpAccountId = refund.IpAccountId,
            AdvanceId = refund.AdvanceId,
            Amount = refund.Amount,
            Mode = refund.Mode!.Value,
            Reference = refund.Reference,
            GatewayRefundId = refund.GatewayRefundId,
            PaidAt = refund.PaidAt!.Value,
            Reason = asked.Reason,
            Note = asked.Note,
            RequestedBy = approval.RequestedBy,
            RequestedByName = approval.RequestedByName,
            ApprovedBy = currentUser.UserId,
            ApprovedByName = currentUser.DisplayName ?? currentUser.UserName,
            ReversedAt = now,
        };
        db.Add(reversal);
        reversal.AnnounceRefundReversed();
        if (refund.GatewayRefundId is { } gatewayRefundId)
        {
            await discrepancies.ClearAsync(DiscrepancyKind.RefundMismatch, gatewayRefundId, $"Refund {refund.RefundNumber} was recorded as not paid out ({reversal.Number}).", ct);
        }

        var text = $"{reversal.Number}: refund {refund.RefundNumber} of ₹{Money.FormatInr(refund.Amount)} by {reversal.Mode} was not paid out — {ReasonWords(asked.Reason)}: {asked.Note} " +
                   $"(asked for by {reversal.RequestedByName}, approved by {reversal.ApprovedByName}); the money is back on the {PurposeWords(refund.Purpose)}.";
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.refund.not_paid_out", text)
        {
            PatientId = refund.PatientId,
            EntityType = nameof(Refund),
            EntityId = refund.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        if (bill is not null)
        {
            audit.Add(new AuditEntry(AuditCategories.Data, "billing.bill.refund_not_paid_out", text)
            {
                PatientId = bill.PatientId,
                EntityType = nameof(Bill),
                EntityId = bill.Id.ToString(),
                Severity = AuditSeverity.Warning,
            });
        }

        return reversal.Number;
    }

    /// <summary>What the reversal of a receipt's lines would do, on the records as they are now, or why it cannot be made.</summary>
    private async Task<Result<Plan>> PlanAsync(Guid receiptId, IReadOnlyCollection<Guid> paymentIds, bool tracking, CancellationToken ct)
    {
        var receipt = await Rows<Receipt>(tracking).Include(r => r.Payments).FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is null)
        {
            return Error.NotFound(nameof(Receipt), receiptId);
        }

        if (ReversalRules.ReceiptRefusal(receipt) is { } refused)
        {
            return refused;
        }

        var chosen = paymentIds.Distinct().ToList();
        if (chosen.Count == 0)
        {
            return Error.Field("paymentIds", "Choose the payments whose money did not arrive.");
        }

        var reversedBefore = await ReversedAsync(receipt.Payments.Select(p => p.Id).ToList(), ct);
        var lines = new List<(ReceiptPayment Line, decimal Amount)>();
        foreach (var id in chosen)
        {
            if (receipt.Payments.FirstOrDefault(p => p.Id == id) is not { } line)
            {
                return Error.Field("paymentIds", "A payment chosen is not on this receipt.");
            }

            var before = reversedBefore.GetValueOrDefault(id);
            if (ReversalRules.LineRefusal(line, before) is { } lineRefused)
            {
                return lineRefused;
            }

            lines.Add((line, line.Amount - before));
        }

        var amount = lines.Sum(l => l.Amount);
        var plan = new Plan(receipt, lines, amount);
        switch (receipt.Purpose)
        {
            case ReceiptPurpose.BillPayment:
                var bill = await Rows<Bill>(tracking).FirstAsync(b => b.Id == receipt.BillId, ct);
                var reversible = bill.CanReversePayment(amount);
                if (reversible.IsFailure)
                {
                    return reversible.Error!;
                }

                plan = plan with { Bill = bill };
                plan = plan with { RefundsToClose = await RefundsToCloseAsync(r => r.Purpose == RefundPurpose.Bill && r.BillId == bill.Id, bill.BalancesAfterReversal(amount).Refundable, tracking, ct) };
                break;

            case ReceiptPurpose.Deposit:
                var account = await Rows<IpAccount>(tracking).FirstAsync(a => a.Id == receipt.IpAccountId, ct);
                plan = plan with { Account = account };
                var deposit = await UndoAsync(plan, account.DepositBalance, r => r.IpAccountId == account.Id && r.AdvanceId == null, tracking, ct);
                if (deposit.IsFailure)
                {
                    return deposit.Error!;
                }

                plan = deposit.Value;
                var depositLeft = account.DepositBalance + plan.Undos.Sum(u => u.Amount) - amount;
                plan = plan with
                {
                    RefundsToClose = await RefundsToCloseAsync(r => r.Purpose == RefundPurpose.Deposit && r.IpAccountId == account.Id, depositLeft, tracking, ct),
                };
                break;

            default:
                var advance = await Rows<PatientAdvance>(tracking).FirstAsync(a => a.ReceiptId == receipt.Id, ct);
                plan = plan with { Advance = advance };
                var advanced = await UndoAsync(plan, advance.Balance, r => r.AdvanceId == advance.Id, tracking, ct);
                if (advanced.IsFailure)
                {
                    return advanced.Error!;
                }

                plan = advanced.Value;
                var advanceLeft = advance.Balance + plan.Undos.Sum(u => u.Amount) - amount;
                plan = plan with
                {
                    RefundsToClose = await RefundsToCloseAsync(r => r.Purpose == RefundPurpose.Advance && r.AdvanceId == advance.Id, advanceLeft, tracking, ct),
                };
                break;
        }

        // A bill an adjustment is taken back from may have a refund waiting that it could then no longer pay.
        foreach (var bill in plan.Undos.GroupBy(u => u.Bill.Id).Select(g => (g.First().Bill, Taken: g.Sum(u => u.Amount))))
        {
            var closing = await RefundsToCloseAsync(r => r.Purpose == RefundPurpose.Bill && r.BillId == bill.Bill.Id, bill.Bill.BalancesAfterReversal(bill.Taken).Refundable, tracking, ct);
            plan = plan with { RefundsToClose = [.. plan.RefundsToClose, .. closing] };
        }

        return plan;
    }

    /// <summary>
    /// The advance adjustments to undo, newest first, so that an advance holding <paramref name="balance"/> can give up
    /// the plan's amount: the bills they paid owe again. Adjustments on cancelled bills went back to the advance with the
    /// cancellation. Refused when the bills cannot give back enough: the rest was refunded, which must be recorded as not
    /// paid out first if it was not.
    /// </summary>
    private async Task<Result<Plan>> UndoAsync(
        Plan plan, decimal balance, System.Linq.Expressions.Expression<Func<Receipt, bool>> adjustmentsOfAdvance, bool tracking, CancellationToken ct)
    {
        var shortfall = plan.Amount - balance;
        if (shortfall <= 0)
        {
            return plan;
        }

        var adjustments = await Rows<Receipt>(tracking).Include(r => r.Payments)
            .Where(r => r.Source == ReceiptSource.DepositAdjustment && r.BillId != null)
            .Where(adjustmentsOfAdvance)
            .OrderByDescending(r => r.ReceivedAt)
            .ToListAsync(ct);
        var reversedBefore = await ReversedAsync(adjustments.SelectMany(a => a.Payments).Select(p => p.Id).ToList(), ct);
        var billIds = adjustments.Select(a => a.BillId!.Value).Distinct().ToList();
        var bills = await Rows<Bill>(tracking).Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var taken = new Dictionary<Guid, decimal>();
        var undos = new List<Undo>();
        foreach (var adjustment in adjustments)
        {
            var bill = bills[adjustment.BillId!.Value];
            if (bill.Status is BillStatus.Cancelled or BillStatus.Refunded)
            {
                continue;
            }

            foreach (var line in adjustment.Payments.Where(p => p.Mode == PaymentMode.Advance))
            {
                var held = bill.PaidAmount - bill.RefundedAmount - taken.GetValueOrDefault(bill.Id);
                var take = Math.Min(Math.Min(line.Amount - reversedBefore.GetValueOrDefault(line.Id), shortfall), held);
                if (take <= 0)
                {
                    continue;
                }

                undos.Add(new Undo(adjustment, line, bill, take));
                taken[bill.Id] = taken.GetValueOrDefault(bill.Id) + take;
                shortfall -= take;
                if (shortfall <= 0)
                {
                    return plan with { Undos = undos };
                }
            }
        }

        return Error.Rule(
            "billing.reversal_exceeds_held",
            $"₹{Money.FormatInr(shortfall)} of this advance was refunded or cannot be taken back from the bills it paid. If a refund from it was not paid out, record that first.");
    }

    /// <summary>The refunds waiting to be paid from a bill or advance that would be left with <paramref name="left"/>: the newest go first.</summary>
    private async Task<IReadOnlyList<Refund>> RefundsToCloseAsync(
        System.Linq.Expressions.Expression<Func<Refund, bool>> ofTarget, decimal left, bool tracking, CancellationToken ct)
    {
        var waiting = await Rows<Refund>(tracking)
            .Where(r => r.Status == RefundStatus.Requested || r.Status == RefundStatus.Approved)
            .Where(ofTarget)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);
        var total = waiting.Sum(r => r.Amount);
        var closing = new List<Refund>();
        foreach (var refund in waiting)
        {
            if (total <= Math.Max(left, 0m))
            {
                break;
            }

            closing.Add(refund);
            total -= refund.Amount;
        }

        return closing;
    }

    /// <summary>What reversals already took back from each of these payment lines.</summary>
    private async Task<Dictionary<Guid, decimal>> ReversedAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken ct) =>
        await db.Set<ReceiptReversalLine>().AsNoTracking()
            .Where(l => paymentIds.Contains(l.ReceiptPaymentId))
            .GroupBy(l => l.ReceiptPaymentId)
            .Select(g => new { g.Key, Amount = g.Sum(l => l.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, ct);

    private async Task<IReadOnlyList<IpAccount>> AccountsOfAsync(Plan plan, CancellationToken ct)
    {
        var ids = plan.Undos.Select(u => u.Bill.IpAccountId)
            .Append(plan.Bill?.IpAccountId)
            .Append(plan.Undos.Count > 0 ? plan.Account?.Id : null)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        return ids.Count == 0 ? [] : await db.Set<IpAccount>().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
    }

    private async Task<ReceiptReversal> NewReceiptReversalAsync(
        Receipt receipt, IReadOnlyList<(ReceiptPayment Line, decimal Amount)> lines, ReceiptReversalPayload asked, ApprovalRequest approval, DateTimeOffset now,
        Guid? causedBy, CancellationToken ct)
    {
        var reversal = new ReceiptReversal
        {
            FacilityId = receipt.FacilityId,
            Number = await numbers.NextAsync(BillingNumberSeries.ReceiptReversal, receipt.FacilityId, ct),
            ReceiptId = receipt.Id,
            ReceiptNumber = receipt.ReceiptNumber,
            PatientId = receipt.PatientId,
            Purpose = receipt.Purpose,
            BillId = receipt.BillId,
            IpAccountId = receipt.IpAccountId,
            AdvanceId = receipt.AdvanceId,
            Amount = lines.Sum(l => l.Amount),
            Reason = asked.Reason,
            Note = asked.Note,
            CausedById = causedBy,
            RequestedBy = approval.RequestedBy,
            RequestedByName = approval.RequestedByName,
            ApprovedBy = currentUser.UserId,
            ApprovedByName = currentUser.DisplayName ?? currentUser.UserName,
            ReversedAt = now,
        };
        reversal.Lines.AddRange(lines.Select(l => new ReceiptReversalLine { ReversalId = reversal.Id, ReceiptPaymentId = l.Line.Id, Mode = l.Line.Mode, Amount = l.Amount }));
        db.Add(reversal);
        return reversal;
    }

    private void Audit(Receipt receipt, ReceiptReversal main, Plan plan)
    {
        var modes = string.Join(" + ", plan.Lines.Select(l => l.Line.Mode).Distinct());
        var effect = receipt.Purpose switch
        {
            ReceiptPurpose.BillPayment => $"bill {plan.Bill!.BillNumber} owes ₹{Money.FormatInr(plan.Bill.DueAmount)}",
            ReceiptPurpose.Deposit => $"the advance of admission {plan.Account!.AdmissionNumber} holds ₹{Money.FormatInr(plan.Account.DepositBalance)}",
            _ => $"advance {plan.Advance!.ReceiptNumber} holds ₹{Money.FormatInr(plan.Advance.Balance)}",
        };
        var bills = plan.Undos.Select(u => u.Bill).DistinctBy(b => b.Id).ToList();
        var undoneText = bills.Count == 0 ? string.Empty : $"; undone with it: {string.Join(", ", bills.Select(b => $"bill {b.BillNumber} (owes ₹{Money.FormatInr(b.DueAmount)})"))}";
        var closedText = plan.RefundsToClose.Count == 0 ? string.Empty : $"; refunds closed: {string.Join(", ", plan.RefundsToClose.Select(r => r.RefundNumber))}";
        var text = $"{main.Number}: ₹{Money.FormatInr(main.Amount)} of receipt {receipt.ReceiptNumber} ({modes}) was not received — {ReasonWords(main.Reason)}: {main.Note} " +
                   $"(asked for by {main.RequestedByName}, approved by {main.ApprovedByName}); {effect}{undoneText}{closedText}.";
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.receipt.reversed", text)
        {
            PatientId = receipt.PatientId,
            EntityType = nameof(Receipt),
            EntityId = receipt.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });

        // Each bill that owes again shows it in its own history.
        foreach (var bill in bills.Prepend(plan.Bill).OfType<Bill>().DistinctBy(b => b.Id))
        {
            audit.Add(new AuditEntry(AuditCategories.Data, "billing.bill.payment_reversed", text)
            {
                PatientId = bill.PatientId,
                EntityType = nameof(Bill),
                EntityId = bill.Id.ToString(),
                Severity = AuditSeverity.Warning,
            });
        }
    }

    private static IReadOnlyList<ApprovalDetail> Details(Plan plan, ReversalReason reason)
    {
        var receipt = plan.Receipt;
        var details = new List<ApprovalDetail>
        {
            new("Receipt", $"{receipt.ReceiptNumber}, {BillingTime.LocalDate(receipt.ReceivedAt):dd MMM yyyy}"),
            new("Not received", string.Join("; ", plan.Lines.Select(l => $"₹{Money.FormatInr(l.Amount)} by {l.Line.Mode}{Evidence(l.Line)}"))),
            new("Why", ReasonWords(reason)),
        };
        switch (receipt.Purpose)
        {
            case ReceiptPurpose.BillPayment:
                details.Add(new("What changes", $"Bill {plan.Bill!.BillNumber} owes ₹{Money.FormatInr(plan.Bill.BalancesAfterReversal(plan.Amount).Due)}"));
                break;
            case ReceiptPurpose.Deposit:
                details.Add(new("What changes", $"The advance of admission {plan.Account!.AdmissionNumber} holds ₹{Money.FormatInr(plan.Account.DepositBalance + plan.Undos.Sum(u => u.Amount) - plan.Amount)}"));
                break;
            default:
                details.Add(new("What changes", $"Advance {plan.Advance!.ReceiptNumber} holds ₹{Money.FormatInr(plan.Advance.Balance + plan.Undos.Sum(u => u.Amount) - plan.Amount)}"));
                break;
        }

        foreach (var bill in plan.Undos.GroupBy(u => u.Bill.Id).Select(g => (g.First().Bill, Taken: g.Sum(u => u.Amount))))
        {
            details.Add(new("Paid from it, owes again", $"Bill {bill.Bill.BillNumber}: ₹{Money.FormatInr(bill.Taken)}, then owes ₹{Money.FormatInr(bill.Bill.BalancesAfterReversal(bill.Taken).Due)}"));
        }

        if (plan.RefundsToClose.Count > 0)
        {
            details.Add(new("Refunds closed unpaid", string.Join(", ", plan.RefundsToClose.Select(r => $"{r.RefundNumber} (₹{Money.FormatInr(r.Amount)})"))));
        }

        return details;
    }

    private static string Evidence(ReceiptPayment line) => line switch
    {
        { ChequeNumber: { } cheque } => $", cheque {cheque} on {line.ChequeBank}",
        { ExternalId: { } id } => $", {id}",
        { Reference: { } reference } => $", {reference}",
        _ => string.Empty,
    };

    private static bool Same(IEnumerable<ReversalStep> now, IReadOnlyList<ReversalStep> asked) =>
        now.OrderBy(s => s.PaymentId).SequenceEqual(asked.OrderBy(s => s.PaymentId));

    internal static string ReasonWords(ReversalReason reason) => reason switch
    {
        ReversalReason.ChequeReturned => "Cheque returned",
        ReversalReason.NotSettled => "Not settled by the bank",
        ReversalReason.TrainingSimulator => "Recorded by a training simulator",
        _ => "Other",
    };

    private static string PurposeWords(RefundPurpose purpose) => purpose switch
    {
        RefundPurpose.Bill => "bill",
        RefundPurpose.Deposit => "in-patient advance",
        _ => "patient advance",
    };

    private IQueryable<T> Rows<T>(bool tracking)
        where T : class => tracking ? db.Set<T>() : db.Set<T>().AsNoTracking();

    /// <summary>A receipt reversal worked out on the records as they are now.</summary>
    private sealed record Plan(Receipt Receipt, IReadOnlyList<(ReceiptPayment Line, decimal Amount)> Lines, decimal Amount)
    {
        public Bill? Bill { get; init; }
        public IpAccount? Account { get; init; }
        public PatientAdvance? Advance { get; init; }
        public IReadOnlyList<Undo> Undos { get; init; } = [];
        public IReadOnlyList<Refund> RefundsToClose { get; init; } = [];
    }

    /// <summary>An advance adjustment taken back from the bill it paid.</summary>
    private sealed record Undo(Receipt Adjustment, ReceiptPayment Line, Bill Bill, decimal Amount);
}
