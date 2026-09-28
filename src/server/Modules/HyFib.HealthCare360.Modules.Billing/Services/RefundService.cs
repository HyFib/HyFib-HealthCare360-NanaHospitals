using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Refunds, credit notes and write-offs (MB-05, OPB-06, IPB-08). A refund is requested, approved by a second person and
/// only then paid out (cash from the open shift, bank/UPI with a reference, or reversed through the payment gateway);
/// credit notes and write-offs are issued by approvers and audited.
/// </summary>
public sealed class RefundService(
    HospitalDbContext db,
    INumberSeries numbers,
    ShiftService shifts,
    INotificationService notifications,
    IAuditLogger audit,
    IServiceProvider services,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<RefundService> logger)
{
    public const string Series = "billing.refund";
    public const string Format = "{FAC}RF{YY}{SEQ:6}";
    public const string CreditNoteSeries = "billing.creditnote";
    public const string CreditNoteFormat = "{FAC}CN{YY}{SEQ:6}";

    public async Task<Result<Guid>> RequestAsync(RequestRefundRequest request, CancellationToken ct)
    {
        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        Refund refund;
        if (request.BillId is { } billId)
        {
            var bill = await db.Set<Bill>().FirstOrDefaultAsync(b => b.Id == billId, ct);
            if (bill is null)
            {
                return Error.NotFound(nameof(Bill), billId);
            }

            var available = bill.RefundableAmount - await OutstandingForBillAsync(billId, ct);
            if (request.Amount > available)
            {
                return Error.Rule("billing.refund_exceeds", $"Only ₹{Money.FormatInr(Math.Max(available, 0m))} can be refunded on this bill.");
            }

            refund = await StageRequestAsync(bill.FacilityId, RefundPurpose.Bill, bill.PatientId, bill.Id, bill.IpAccountId, request.Amount, request.Reason, ct);
        }
        else if (request.AdvanceId is { } advanceId)
        {
            var advance = await db.Set<PatientAdvance>().FirstOrDefaultAsync(a => a.Id == advanceId, ct);
            if (advance is null)
            {
                return Error.NotFound(nameof(PatientAdvance), advanceId);
            }

            var available = advance.Balance - await OutstandingForAdvanceAsync(advanceId, ct);
            if (request.Amount > available)
            {
                return Error.Rule("billing.refund_exceeds", $"Only ₹{Money.FormatInr(Math.Max(available, 0m))} of the advance can be refunded.");
            }

            refund = await StageRequestAsync(
                advance.FacilityId, RefundPurpose.Advance, advance.PatientId, null, null, request.Amount, request.Reason, ct, advanceId: advance.Id);
        }
        else
        {
            var accountId = request.IpAccountId!.Value;
            var account = await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null)
            {
                return Error.NotFound(nameof(IpAccount), accountId);
            }

            var available = account.DepositBalance - await OutstandingForAccountAsync(accountId, ct);
            if (request.Amount > available)
            {
                return Error.Rule("billing.refund_exceeds", $"Only ₹{Money.FormatInr(Math.Max(available, 0m))} of the advance can be refunded.");
            }

            refund = await StageRequestAsync(account.FacilityId, RefundPurpose.Deposit, account.PatientId, null, account.Id, request.Amount, request.Reason, ct);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotifyApproversAsync(refund, ct);
        return refund.Id;
    }

    public async Task<Result> ApproveAsync(Guid id, string? remarks, CancellationToken ct)
    {
        var refund = await db.Set<Refund>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (refund is null)
        {
            return Error.NotFound(nameof(Refund), id);
        }

        if (currentUser.UserId is not { } approverId)
        {
            return BillingDb.SignInRequired;
        }

        var approved = refund.Approve(approverId, remarks, time.GetUtcNow());
        if (approved.IsFailure)
        {
            return approved;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "billing.refund.approved", $"Approved refund {refund.RefundNumber} of ₹{Money.FormatInr(refund.Amount)}")
        {
            PatientId = refund.PatientId,
            EntityType = nameof(Refund),
            EntityId = refund.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        await NotifyRequesterAsync(refund, "Refund approved", ct);
        return Result.Success();
    }

    public async Task<Result> RejectAsync(Guid id, string reason, CancellationToken ct)
    {
        var refund = await db.Set<Refund>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (refund is null)
        {
            return Error.NotFound(nameof(Refund), id);
        }

        if (currentUser.UserId is not { } approverId)
        {
            return BillingDb.SignInRequired;
        }

        var rejected = refund.Reject(approverId, reason, time.GetUtcNow());
        if (rejected.IsFailure)
        {
            return rejected;
        }

        await db.SaveChangesAsync(ct);
        await NotifyRequesterAsync(refund, "Refund rejected", ct);
        return Result.Success();
    }

    /// <summary>Pays out an approved refund and reduces the bill's refundable balance, the IP advance or the patient advance.</summary>
    public async Task<Result> PayAsync(Guid id, PayRefundRequest request, CancellationToken ct)
    {
        var refund = await db.Set<Refund>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (refund is null)
        {
            return Error.NotFound(nameof(Refund), id);
        }

        if (refund.Status != RefundStatus.Approved)
        {
            return Error.Rule("billing.refund_not_approved", "Only approved refunds can be paid out.");
        }

        if (request.Mode is not (PaymentMode.Cash or PaymentMode.PaymentGateway) && string.IsNullOrWhiteSpace(request.Reference))
        {
            return Error.Field("reference", "Enter the transfer, reversal or cheque reference.");
        }

        var shift = request.Mode == PaymentMode.Cash ? await shifts.RequireOpenShiftAsync(ct) : Result<CashierShift?>.Ok(await shifts.CurrentAsync(ct));
        if (shift.IsFailure)
        {
            return shift.Error!;
        }

        Bill? bill = null;
        IpAccount? account = null;
        PatientAdvance? advance = null;
        switch (refund.Purpose)
        {
            case RefundPurpose.Bill:
                bill = await db.Set<Bill>().FirstAsync(b => b.Id == refund.BillId, ct);
                if (refund.Amount > bill.RefundableAmount)
                {
                    return Error.Rule("billing.refund_exceeds", $"Only ₹{Money.FormatInr(bill.RefundableAmount)} is refundable on this bill now.");
                }

                break;

            case RefundPurpose.Advance:
                advance = await db.Set<PatientAdvance>().FirstAsync(a => a.Id == refund.AdvanceId, ct);
                if (refund.Amount > advance.Balance)
                {
                    return Error.Rule("billing.advance_insufficient", $"The advance balance is ₹{Money.FormatInr(Math.Max(advance.Balance, 0m))}.");
                }

                break;

            default:
                account = await db.Set<IpAccount>().FirstAsync(a => a.Id == refund.IpAccountId, ct);
                if (refund.Amount > account.DepositBalance)
                {
                    return Error.Rule("billing.deposit_insufficient", $"The advance balance is ₹{Money.FormatInr(account.DepositBalance)}.");
                }

                break;
        }

        var reference = request.Reference;
        if (request.Mode == PaymentMode.PaymentGateway)
        {
            var reversal = await RefundThroughGatewayAsync(refund, ct);
            if (reversal.IsFailure)
            {
                return reversal.Error!;
            }

            reference = reversal.Value;
            refund.GatewayRefundId = reversal.Value;
        }

        var paid = refund.MarkPaid(request.Mode, reference, currentUser.UserId, shift.Value?.Id, shift.Value?.Counter, time.GetUtcNow());
        if (paid.IsFailure)
        {
            return paid;
        }

        var applied = bill is not null ? bill.ApplyRefund(refund.Amount)
            : advance is not null ? advance.Refund(refund.Amount)
            : account!.RefundDeposit(refund.Amount);
        if (applied.IsFailure)
        {
            return applied;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "billing.refund.paid", $"Paid refund {refund.RefundNumber} of ₹{Money.FormatInr(refund.Amount)} by {request.Mode}")
        {
            PatientId = refund.PatientId,
            EntityType = nameof(Refund),
            EntityId = refund.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (refund.GatewayRefundId is not null)
        {
            // The gateway has already returned the money; make the reconciliation trail impossible to miss.
            logger.LogCritical(ex, "Gateway refund {GatewayRefundId} for {RefundNumber} succeeded but could not be recorded", refund.GatewayRefundId, refund.RefundNumber);
            throw;
        }

        return Result.Success();
    }

    public async Task<Result<Guid>> IssueCreditNoteAsync(Guid billId, IssueCreditNoteRequest request, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().FirstOrDefaultAsync(b => b.Id == billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        var credited = bill.ApplyCredit(Money.Round(request.Amount));
        if (credited.IsFailure)
        {
            return credited.Error!;
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var note = new CreditNote
        {
            FacilityId = bill.FacilityId,
            Number = await numbers.NextAsync(CreditNoteSeries, bill.FacilityId, CreditNoteFormat, NumberResetPolicy.Yearly, ct),
            Kind = request.Kind,
            BillId = bill.Id,
            PatientId = bill.PatientId,
            Amount = Money.Round(request.Amount),
            Reason = request.Reason.Trim(),
            IssuedBy = currentUser.UserId,
            IssuedByName = currentUser.DisplayName ?? currentUser.UserName,
            IssuedAt = time.GetUtcNow(),
        };
        db.Add(note);
        audit.Add(new AuditEntry(AuditCategories.Data, request.Kind == CreditNoteKind.WriteOff ? "billing.bill.written_off" : "billing.credit_note.issued",
            $"{note.Number}: ₹{Money.FormatInr(note.Amount)} on bill {bill.BillNumber} — {note.Reason}")
        {
            PatientId = bill.PatientId,
            EntityType = nameof(Bill),
            EntityId = bill.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return note.Id;
    }

    /// <summary>
    /// Creates a refund request inside the caller's transaction (number allocated there). Without a signed-in user (a
    /// system request, for example a cancelled prepaid appointment) any holder of the approve permission may approve it.
    /// The caller saves.
    /// </summary>
    internal async Task<Refund> StageRequestAsync(
        Guid facilityId, RefundPurpose purpose, Guid patientId, Guid? billId, Guid? ipAccountId, decimal amount, string reason, CancellationToken ct,
        Guid? advanceId = null)
    {
        var number = await numbers.NextAsync(Series, facilityId, Format, NumberResetPolicy.Yearly, ct);
        var refund = Refund.Request(
            facilityId, number, purpose, patientId, billId, ipAccountId, amount, reason, currentUser.UserId, currentUser.DisplayName ?? currentUser.UserName, time.GetUtcNow());
        refund.AdvanceId = advanceId;
        db.Add(refund);
        return refund;
    }

    /// <summary>
    /// Returns an advance adjusted on a cancelled bill to the IP deposit or the patient advance it came from (immediately
    /// complete, no money moves). The caller saves.
    /// </summary>
    internal async Task<Refund> StageAdvanceReturnAsync(Bill bill, decimal amount, string reason, CancellationToken ct, Guid? advanceId = null)
    {
        var number = await numbers.NextAsync(Series, bill.FacilityId, Format, NumberResetPolicy.Yearly, ct);
        var refund = Refund.ReturnToAdvance(bill.FacilityId, number, bill.PatientId, bill.Id, bill.IpAccountId, amount, reason, currentUser.UserId, time.GetUtcNow());
        refund.AdvanceId = advanceId;
        db.Add(refund);
        return refund;
    }

    internal async Task<decimal> OutstandingForAdvanceAsync(Guid advanceId, CancellationToken ct) =>
        await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.AdvanceId == advanceId && r.Purpose == RefundPurpose.Advance && (r.Status == RefundStatus.Requested || r.Status == RefundStatus.Approved))
            .SumAsync(r => r.Amount, ct)
        + db.Set<Refund>().Local
            .Where(r => r.AdvanceId == advanceId && r.Purpose == RefundPurpose.Advance && r.IsOutstanding && db.Entry(r).State == EntityState.Added)
            .Sum(r => r.Amount);

    internal Task<decimal> OutstandingForBillAsync(Guid billId, CancellationToken ct) =>
        db.Set<Refund>()
            .Where(r => r.BillId == billId && r.Purpose == RefundPurpose.Bill && (r.Status == RefundStatus.Requested || r.Status == RefundStatus.Approved))
            .SumAsync(r => r.Amount, ct);

    internal Task<decimal> OutstandingForAccountAsync(Guid ipAccountId, CancellationToken ct) =>
        db.Set<Refund>()
            .Where(r => r.IpAccountId == ipAccountId && r.Purpose == RefundPurpose.Deposit && (r.Status == RefundStatus.Requested || r.Status == RefundStatus.Approved))
            .SumAsync(r => r.Amount, ct);

    private async Task<Result<string>> RefundThroughGatewayAsync(Refund refund, CancellationToken ct)
    {
        var gateway = services.GetService<IPaymentGateway>();
        if (gateway is null)
        {
            return Error.Rule("billing.gateway_not_configured", "No payment gateway is configured. Refund by another mode.");
        }

        // An online patient advance (appointment prepayment) is reversed on its own payment; bills and IP advances on the
        // latest paid payment link or checkout large enough to cover the refund.
        var paymentId = refund.Purpose == RefundPurpose.Advance
            ? await db.Set<PatientAdvance>()
                .Where(a => a.Id == refund.AdvanceId && a.Amount - a.RefundedAmount >= refund.Amount)
                .Select(a => a.GatewayPaymentId)
                .FirstOrDefaultAsync(ct)
            : await db.Set<GatewayOrder>()
                .Where(o => (refund.BillId != null && o.BillId == refund.BillId) || (refund.IpAccountId != null && o.IpAccountId == refund.IpAccountId))
                .Where(o => o.Status == GatewayOrderStatus.Paid && o.GatewayPaymentId != null && o.PaidAmount >= refund.Amount)
                .OrderByDescending(o => o.PaidAt)
                .Select(o => o.GatewayPaymentId)
                .FirstOrDefaultAsync(ct);
        if (paymentId is null)
        {
            return Error.Rule("billing.gateway_payment_missing", "No online payment large enough to reverse was found. Refund by another mode.");
        }

        var result = await gateway.RefundAsync(paymentId, refund.Amount, refund.Reason, refund.FacilityId, ct);
        if (!result.Success || string.IsNullOrWhiteSpace(result.RefundId))
        {
            return Error.Rule("billing.gateway_refund_failed", result.Error ?? "The payment gateway declined the refund.");
        }

        return result.RefundId;
    }

    internal Task NotifyApproversAsync(Refund refund, CancellationToken ct) =>
        notifications.NotifyPermissionHoldersAsync(
            BillingPermissions.RefundsApprove,
            refund.FacilityId,
            "Refund awaiting approval",
            $"{refund.RefundNumber}: ₹{Money.FormatInr(refund.Amount)} — {refund.Reason}",
            $"/billing/refunds/{refund.Id}",
            "billing",
            "warning",
            ct);

    private Task NotifyRequesterAsync(Refund refund, string title, CancellationToken ct) =>
        refund.RequestedBy is { } requester
            ? notifications.NotifyUsersAsync([requester], title, $"{refund.RefundNumber}: ₹{Money.FormatInr(refund.Amount)}", $"/billing/refunds/{refund.Id}", "billing", "info", ct)
            : Task.CompletedTask;
}
