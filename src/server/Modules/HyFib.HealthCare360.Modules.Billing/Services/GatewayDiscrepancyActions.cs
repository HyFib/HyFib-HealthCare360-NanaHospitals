using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// What an accountant can do about an exception besides resolving it with a note: record the receipt for money the
/// gateway took (against its bill, admission or booking, or else as a patient advance), and try again (ask the gateway
/// again, read the settlements again, or send a failed refund again). Every amount and time comes from the gateway,
/// never from the screen. Each action that records money is audited.
/// </summary>
public sealed class GatewayDiscrepancyActions(
    HospitalDbContext db,
    PaymentGatewayService payments,
    PrepaymentChecker prepayments,
    AdvanceService advances,
    GatewayRefundTracker refundTracker,
    GatewaySettlementService settlements,
    GatewayDiscrepancyService discrepancies,
    TallyCorrections tally,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>Settlements are read again for at most this many days when an unsettled receipt is checked again.</summary>
    private const int RecheckDays = 5;

    /// <summary>
    /// Records the receipt for money the gateway took that no receipt records. The gateway is asked first; if the bill
    /// or admission can no longer take the money (or the payment was made on an older order of a booking), it is kept as
    /// a patient advance, to be adjusted or refunded.
    /// </summary>
    public async Task<Result<DiscrepancyActionResult>> RecordReceiptAsync(Guid id, CancellationToken ct)
    {
        var discrepancy = await db.Set<GatewayDiscrepancy>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (discrepancy is null)
        {
            return Error.NotFound(nameof(GatewayDiscrepancy), id);
        }

        if (discrepancy is not { Kind: DiscrepancyKind.UnrecordedPayment, Status: DiscrepancyStatus.Open })
        {
            return Error.Rule("billing.discrepancy_action", "Only an open payment without a receipt can be recorded.");
        }

        var paymentId = discrepancy.Key;
        var outcome = await RecordAsync(discrepancy, allowAdvance: true, ct);
        if (outcome.IsFailure)
        {
            return outcome.Error!;
        }

        return await FinishAsync(discrepancy.Id, paymentId, outcome.Value, ct);
    }

    /// <summary>Tries again whatever can be tried for the exception's kind; says what happened.</summary>
    public async Task<Result<DiscrepancyActionResult>> RetryAsync(Guid id, CancellationToken ct)
    {
        var discrepancy = await db.Set<GatewayDiscrepancy>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (discrepancy is null)
        {
            return Error.NotFound(nameof(GatewayDiscrepancy), id);
        }

        if (discrepancy.Status != DiscrepancyStatus.Open)
        {
            return Error.Rule("billing.discrepancy_closed", "This exception is already resolved.");
        }

        switch (discrepancy.Kind)
        {
            case DiscrepancyKind.UnrecordedPayment:
                var paymentId = discrepancy.Key;
                var recorded = await RecordAsync(discrepancy, allowAdvance: false, ct);
                return recorded.IsFailure ? recorded.Error! : await FinishAsync(discrepancy.Id, paymentId, recorded.Value, ct);

            case DiscrepancyKind.UnsettledReceipt:
                return await ReadSettlementsAgainAsync(discrepancy, ct);

            case DiscrepancyKind.RefundMismatch when discrepancy.RefundId is { } refundId:
                return await RetryRefundAsync(discrepancy, refundId, ct);

            default:
                return await MatchAgainAsync(discrepancy, ct);
        }
    }

    /// <summary>Records the payment where it belongs; returns what was recorded, in words.</summary>
    private async Task<Result<string>> RecordAsync(GatewayDiscrepancy discrepancy, bool allowAdvance, CancellationToken ct)
    {
        var paymentId = discrepancy.Key;
        if (discrepancy.GatewayOrderRecordId is { } orderRecordId)
        {
            var order = await db.Set<GatewayOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .FirstOrDefaultAsync(o => o.Id == orderRecordId && o.FacilityId == discrepancy.FacilityId, ct);
            if (order is null)
            {
                return Error.NotFound(nameof(GatewayOrder), orderRecordId);
            }

            if (order.Status != GatewayOrderStatus.Paid)
            {
                var synced = await payments.SyncAsync(order, ct);
                if (synced.IsFailure)
                {
                    return synced.Error!;
                }
            }

            if (await ReceiptForAsync(paymentId, ct) is { } receipt)
            {
                return $"Receipt {receipt.Number} recorded.";
            }

            if (!allowAdvance)
            {
                return Error.Rule("billing.receipt_not_recorded", order.LastError ?? "The gateway does not report this payment as captured on its order yet.");
            }

            return await KeepAsAdvanceAsync(
                discrepancy, order.FacilityId, order.PatientId, order.OrderId, order.Provider, null, null,
                $"Online payment {paymentId} that could not be applied where it was meant to go", ct);
        }

        if (discrepancy.AppointmentId is { } appointmentId)
        {
            var booking = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .FirstOrDefaultAsync(a => a.Id == appointmentId && a.FacilityId == discrepancy.FacilityId, ct);
            if (booking is null)
            {
                return Error.NotFound(nameof(Appointment), appointmentId);
            }

            var ownPayment = booking.GatewayPaymentId == paymentId
                || (booking.PaymentStatus != AppointmentPaymentStatus.PaidOnline && (discrepancy.OrderId is null || booking.GatewayOrderId == discrepancy.OrderId));
            if (ownPayment)
            {
                var confirmed = await prepayments.ConfirmAsync(booking, ct);
                if (confirmed.IsSuccess && booking.GatewayPaymentId == paymentId)
                {
                    return $"Booking {booking.Number} confirmed as paid; advance receipt {confirmed.Value} recorded.";
                }

                if (confirmed.IsFailure && !allowAdvance)
                {
                    return confirmed.Error!;
                }
            }

            if (!allowAdvance)
            {
                return Error.Rule("billing.receipt_not_recorded", "This payment was made on an older order of the booking. Record the receipt to keep it as an advance.");
            }

            return await KeepAsAdvanceAsync(
                discrepancy, booking.FacilityId, booking.PatientId, discrepancy.OrderId, booking.PaymentProvider, booking.Id, booking.Number,
                $"Online payment {paymentId} for appointment {booking.Number}", ct);
        }

        return Error.Rule(
            "billing.payment_unlinked",
            "This payment is not linked to a bill, an admission or an appointment in HC360. Find out what it was for, record it at the counter if needed, and mark the exception resolved with a note.");
    }

    /// <summary>The money is kept on the patient's account as an advance, dated when the gateway took it.</summary>
    private async Task<Result<string>> KeepAsAdvanceAsync(
        GatewayDiscrepancy discrepancy, Guid facilityId, Guid patientId, string? orderId, string? provider, Guid? appointmentId, string? appointmentNumber,
        string notes, CancellationToken ct)
    {
        if (discrepancy.GatewayAmount is not > 0m)
        {
            return Error.Rule("billing.amount_unknown", "The gateway has not reported the amount of this payment yet. Try again after the next settlement.");
        }

        var paidAt = discrepancy.GatewayAt is { } at && at < time.GetUtcNow() ? at : time.GetUtcNow();
        PatientAdvance advance;
        await using (var transaction = await db.BeginOwnedTransactionAsync(ct))
        {
            advance = await advances.RecordOnlinePaymentAsync(
                facilityId, patientId, discrepancy.GatewayAmount.Value, discrepancy.Key, orderId, provider, notes, paidAt, appointmentId, appointmentNumber, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        await tally.RepostIfPostedAsync(facilityId, BillingTime.LocalDate(paidAt), ct);
        return $"Kept as advance receipt {advance.ReceiptNumber} for the patient. Adjust it on a bill or refund it.";
    }

    /// <summary>
    /// Closes the exception in the name of the person who acted, links the receipt, and matches the settled transaction
    /// again. The exception is read again because recording may have restarted the unit of work.
    /// </summary>
    private async Task<Result<DiscrepancyActionResult>> FinishAsync(Guid discrepancyId, string paymentId, string message, CancellationToken ct)
    {
        var discrepancy = await db.Set<GatewayDiscrepancy>().FirstAsync(d => d.Id == discrepancyId, ct);
        var receipt = await ReceiptForAsync(paymentId, ct);
        discrepancy.ReceiptId = receipt?.Id ?? discrepancy.ReceiptId;
        discrepancy.HandledBy(currentUser.UserId, currentUser.DisplayName ?? currentUser.UserName, message, time.GetUtcNow());
        await settlements.RematchPaymentAsync(paymentId, ct);
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.gateway.receipt_recorded", $"Online payment {paymentId}: {message}")
        {
            PatientId = receipt?.PatientId,
            EntityType = nameof(GatewayDiscrepancy),
            EntityId = discrepancy.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return await ResultAsync(discrepancy.Id, message, ct);
    }

    /// <summary>Reads the settlements of the days after the payment again; a match clears the exception.</summary>
    private async Task<Result<DiscrepancyActionResult>> ReadSettlementsAgainAsync(GatewayDiscrepancy discrepancy, CancellationToken ct)
    {
        var today = BillingTime.Today(time);
        var from = discrepancy.GatewayAt is { } paidAt ? BillingTime.LocalDate(paidAt) : today.AddDays(-RecheckDays + 1);
        var days = Enumerable.Range(0, RecheckDays).Select(i => from.AddDays(i)).Where(d => d <= today).ToList();
        string? problem = null;
        foreach (var day in days)
        {
            var fetched = await settlements.FetchAsync(discrepancy.FacilityId, day, ct);
            problem ??= fetched.IsFailure ? fetched.Error!.Message : null;
        }

        db.ChangeTracker.Clear();
        var current = await db.Set<GatewayDiscrepancy>().AsNoTracking().FirstAsync(d => d.Id == discrepancy.Id, ct);
        var message = current.Status == DiscrepancyStatus.Resolved
            ? current.Resolution ?? "Settled."
            : problem ?? $"Still not in any settlement of {days[0]:dd MMM} to {days[^1]:dd MMM}.";
        return await ResultAsync(discrepancy.Id, message, ct);
    }

    private async Task<Result<DiscrepancyActionResult>> RetryRefundAsync(GatewayDiscrepancy discrepancy, Guid refundId, CancellationToken ct)
    {
        var refund = await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(r => r.Id == refundId && r.FacilityId == discrepancy.FacilityId, ct);
        if (refund is null)
        {
            return Error.NotFound(nameof(Refund), refundId);
        }

        if (refund.GatewayStatus == PaymentRefundStatus.Failed)
        {
            var sent = await refundTracker.RetryAsync(refund, ct);
            return sent.IsFailure
                ? sent.Error!
                : await ResultAsync(discrepancy.Id, $"Refund {refund.RefundNumber} sent again as {sent.Value}. It is followed until the bank confirms it.", ct);
        }

        if (refund.IsGatewayRefundOpen)
        {
            await refundTracker.FollowAsync(refund, ct);
        }

        return await MatchAgainAsync(discrepancy, ct, $"Refund {refund.RefundNumber}: {Describe(refund.GatewayStatus)}.");
    }

    /// <summary>Matches the settled transaction again (the receipt or refund may have been corrected since).</summary>
    private async Task<Result<DiscrepancyActionResult>> MatchAgainAsync(GatewayDiscrepancy discrepancy, CancellationToken ct, string? prefix = null)
    {
        if (discrepancy.SettlementLineId is not { } lineId)
        {
            return Error.Rule("billing.nothing_to_retry", "These figures come from the gateway as they are. Check them against the bank statement and resolve the exception with a note.");
        }

        var line = await db.Set<GatewaySettlementLine>().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        var settlement = line is null ? null : await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == line.GatewaySettlementId, ct);
        if (line is null || settlement is null)
        {
            return Error.NotFound(nameof(GatewaySettlementLine), lineId);
        }

        var stored = settlement.Lines.First(l => l.Id == lineId);
        await settlements.MatchAsync([stored], ct);
        await db.SaveChangesAsync(ct);
        await settlements.RaiseExceptionsAsync([settlement], ct);
        var current = await db.Set<GatewayDiscrepancy>().AsNoTracking().FirstAsync(d => d.Id == discrepancy.Id, ct);
        var message = current.Status == DiscrepancyStatus.Resolved ? current.Resolution ?? "It matches now." : "It still does not match.";
        return await ResultAsync(discrepancy.Id, prefix is null ? message : $"{prefix} {message}", ct);
    }

    private async Task<Result<DiscrepancyActionResult>> ResultAsync(Guid id, string message, CancellationToken ct)
    {
        var dto = await discrepancies.GetAsync(id, ct);
        return dto is null ? Error.NotFound(nameof(GatewayDiscrepancy), id) : new DiscrepancyActionResult(message, dto);
    }

    private async Task<RecordedReceipt?> ReceiptForAsync(string paymentId, CancellationToken ct) =>
        await (from p in db.Set<ReceiptPayment>()
               join r in db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.ReceiptId equals r.Id
               where p.Mode == PaymentMode.PaymentGateway && p.ExternalId == paymentId
               select new RecordedReceipt(r.Id, r.ReceiptNumber, r.PatientId))
            .FirstOrDefaultAsync(ct);

    private static string Describe(PaymentRefundStatus? status) => status switch
    {
        PaymentRefundStatus.Processed => "the bank has returned the money",
        PaymentRefundStatus.Failed => "the gateway could not return the money",
        _ => "the bank has not confirmed it yet",
    };

    private sealed record RecordedReceipt(Guid Id, string Number, Guid PatientId);
}
