using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>A payment already captured outside the counter (POS terminal or payment gateway).</summary>
/// <param name="ReceivedAt">When the money was taken (the gateway's capture time); null means now.</param>
internal sealed record ExternalPayment(
    Guid FacilityId,
    ReceiptPurpose Purpose,
    Guid PatientId,
    Guid? BillId,
    Guid? IpAccountId,
    PaymentMode Mode,
    decimal Amount,
    string ExternalId,
    string? Reference,
    string? CardLast4,
    ReceiptSource Source,
    CashierShift? Shift,
    DateTimeOffset? ReceivedAt = null);

/// <summary>
/// Receipts (MB-04, OPB-05/06, IPB-01): split payments at the counter, IP advances, POS/gateway captures, advance
/// adjustments and payer settlements. Every receipt number is allocated inside the transaction that records the money,
/// the patient is sent the "bill.receipt" SMS through the outbox (and the bill on WhatsApp once a payment settles it,
/// see <see cref="BillSharing"/>), and PaymentReceived is raised for patient money. Cash at the counter is held to the
/// Income-tax Act's limit (<see cref="CashLimit"/>): a bill of an admission counts with the admission.
/// </summary>
public sealed class ReceiptService(
    HospitalDbContext db,
    INumberSeries numbers,
    ShiftService shifts,
    INotificationService notifications,
    BillSharing bills,
    IRealtimePublisher realtime,
    CashLimit cashLimit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<Result<Guid>> CollectForBillAsync(CollectPaymentRequest request, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().FirstOrDefaultAsync(b => b.Id == request.BillId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), request.BillId);
        }

        var lines = ValidateCounterLines(request.Payments);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        if (!bill.IsOpenForPayment)
        {
            return Error.Rule("billing.bill_not_payable", "This bill has no balance to collect.");
        }

        var shift = await shifts.RequireOpenShiftAsync(ct);
        if (shift.IsFailure)
        {
            return shift.Error!;
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var cash = await CheckCashLimitAsync(bill.FacilityId, bill.PatientId, bill, null, BillingCashReceipts.CashIn(lines.Value), time.GetUtcNow(), ct);
        if (cash.IsFailure)
        {
            return cash.Error!;
        }

        var receipt = await NewReceiptAsync(bill.FacilityId, ReceiptPurpose.BillPayment, bill.PatientId, ReceiptSource.Counter, shift.Value, ct);
        receipt.BillId = bill.Id;
        receipt.VisitId = bill.VisitId;
        receipt.AdmissionId = bill.AdmissionId;
        receipt.IpAccountId = bill.IpAccountId;
        receipt.Notes = Clean(request.Notes);
        foreach (var line in lines.Value)
        {
            receipt.AddPayment(line);
        }

        var recorded = receipt.Record(time.GetUtcNow());
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        var paid = bill.ApplyPayment(receipt.Amount, allowExcess: false);
        if (paid.IsFailure)
        {
            return paid.Error!;
        }

        db.Add(receipt);
        await QueueReceiptMessageAsync(receipt, bill.BillNumber, bill, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.ToFacilityAsync(bill.FacilityId, "billing.bill.changed", new { bill.Id, bill.PatientId, bill.Status }, ct);
        return receipt.Id;
    }

    /// <summary>In-patient advance (IPB-01) collected at the counter.</summary>
    public async Task<Result<Guid>> CollectDepositAsync(Guid ipAccountId, CollectDepositRequest request, CancellationToken ct)
    {
        var account = await db.Set<IpAccount>().FirstOrDefaultAsync(a => a.Id == ipAccountId, ct);
        if (account is null)
        {
            return Error.NotFound(nameof(IpAccount), ipAccountId);
        }

        if (!account.IsActive)
        {
            return Error.Rule("billing.ip_account_inactive", "Advances can be collected only while the patient is admitted.");
        }

        // The victim of a sexual offence or an acid attack is treated free of cost (BNSS 2023, s.397): no advance is asked.
        if (await BillFreeTreatment.ReasonAsync(db, account.VisitId, ct) is { } free)
        {
            return Error.Rule("billing.free_treatment", $"This stay is free of cost by law ({free}), so no advance is taken.");
        }

        var lines = ValidateCounterLines(request.Payments);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        var shift = await shifts.RequireOpenShiftAsync(ct);
        if (shift.IsFailure)
        {
            return shift.Error!;
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var cash = await CheckCashLimitAsync(account.FacilityId, account.PatientId, null, account, BillingCashReceipts.CashIn(lines.Value), time.GetUtcNow(), ct);
        if (cash.IsFailure)
        {
            return cash.Error!;
        }

        var receipt = await NewReceiptAsync(account.FacilityId, ReceiptPurpose.Deposit, account.PatientId, ReceiptSource.Counter, shift.Value, ct);
        receipt.IpAccountId = account.Id;
        receipt.AdmissionId = account.AdmissionId;
        receipt.VisitId = account.VisitId;
        receipt.Notes = Clean(request.Notes);
        foreach (var line in lines.Value)
        {
            receipt.AddPayment(line);
        }

        var recorded = receipt.Record(time.GetUtcNow());
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        var deposited = account.RecordDeposit(receipt.Amount);
        if (deposited.IsFailure)
        {
            return deposited.Error!;
        }

        db.Add(receipt);
        await QueueReceiptMessageAsync(receipt, $"advance, admission {account.AdmissionNumber}", null, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return receipt.Id;
    }

    /// <summary>
    /// Records money captured by a POS terminal or the gateway. Idempotent on (mode, external id); an amount above the
    /// balance is still recorded (the money was received) and the excess becomes refundable. The receipt is dated when
    /// the money was taken (<see cref="ExternalPayment.ReceivedAt"/>, never later than now), so a payment recorded late
    /// still belongs to the day the patient paid. The caller owns the transaction and saves.
    /// </summary>
    internal async Task<Result<Receipt>> RecordExternalAsync(ExternalPayment payment, CancellationToken ct)
    {
        var existing = await db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(r => r.Payments)
            .FirstOrDefaultAsync(r => r.Payments.Any(p => p.Mode == payment.Mode && p.ExternalId == payment.ExternalId), ct);
        if (existing is not null)
        {
            return existing;
        }

        Bill? bill = null;
        IpAccount? account = null;
        if (payment.BillId is { } billId)
        {
            bill = await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(b => b.Id == billId, ct);
            if (bill is null)
            {
                return Error.NotFound(nameof(Bill), billId);
            }
        }
        else if (payment.IpAccountId is { } accountId)
        {
            account = await db.Set<IpAccount>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null)
            {
                return Error.NotFound(nameof(IpAccount), accountId);
            }

            // A link or a terminal sale for an advance can be paid after the stay was marked free of cost by law (BNSS
            // 2023, s.397). Refused, the advance is not recorded: the capture is left to the accountants, to refund.
            if (await BillFreeTreatment.ReasonAsync(db, account.VisitId, ct) is { } free)
            {
                return Error.Rule("billing.free_treatment", $"This stay is free of cost by law ({free}), so no advance is taken: refund the payment.");
            }
        }
        else
        {
            return Error.Rule("billing.payment_target", "The payment is not linked to a bill or an IP account.");
        }

        // A payment made online from a wallet is held to the cash limit too, on the day the gateway took it. Refused, it is
        // not recorded: the gateway's capture is left to the accountants, to refund.
        var line = new PaymentLineData(payment.Mode, payment.Amount, payment.Reference, payment.CardLast4, ExternalId: payment.ExternalId);
        var now = time.GetUtcNow();
        var receivedAt = payment.ReceivedAt is { } at && at < now ? at : now;
        var cash = await CheckCashLimitAsync(payment.FacilityId, payment.PatientId, bill, account, BillingCashReceipts.CashIn([line]), receivedAt, ct);
        if (cash.IsFailure)
        {
            return cash.Error!;
        }

        var receipt = await NewReceiptAsync(payment.FacilityId, payment.Purpose, payment.PatientId, payment.Source, payment.Shift, ct);
        receipt.BillId = bill?.Id;
        receipt.VisitId = bill?.VisitId ?? account?.VisitId;
        receipt.AdmissionId = bill?.AdmissionId ?? account?.AdmissionId;
        receipt.IpAccountId = bill?.IpAccountId ?? account?.Id;
        receipt.AddPayment(line);
        var recorded = receipt.Record(receivedAt);
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        var applied = bill is not null ? bill.ApplyPayment(receipt.Amount, allowExcess: true) : account!.RecordDeposit(receipt.Amount);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Add(receipt);
        await QueueReceiptMessageAsync(receipt, bill?.BillNumber ?? $"advance, admission {account?.AdmissionNumber}", bill, ct);
        return receipt;
    }

    /// <summary>
    /// The cash limit (<see cref="CashLimit"/>) for <paramref name="cash"/> received at <paramref name="receivedAt"/>, paid on
    /// <paramref name="bill"/> or as an advance on <paramref name="account"/>: a bill of an admission counts with the
    /// admission, any other bill on its own.
    /// </summary>
    private Task<Result> CheckCashLimitAsync(
        Guid facilityId, Guid patientId, Bill? bill, IpAccount? account, decimal cash, DateTimeOffset receivedAt, CancellationToken ct)
    {
        if ((bill?.IpAccountId ?? account?.Id) is { } accountId)
        {
            return cashLimit.CheckAsync(facilityId, patientId, cash, "admission",
                c => BillingCashReceipts.CashTakenAsync(db, r => r.IpAccountId == accountId, c), receivedAt, ct);
        }

        var billId = bill!.Id;
        return cashLimit.CheckAsync(facilityId, patientId, cash, "bill", c => BillingCashReceipts.CashTakenAsync(db, r => r.BillId == billId, c), receivedAt, ct);
    }

    /// <summary>Adjusts part of the IP advance against a bill (no money moves; journal in Tally). The caller saves.</summary>
    internal async Task<Result<Receipt>> AdjustAdvanceAsync(Bill bill, IpAccount account, decimal amount, CancellationToken ct)
    {
        if (!bill.IsOpenForPayment || amount > bill.DueAmount)
        {
            return Error.Rule("billing.overpayment", $"The balance on this bill is ₹{Money.FormatInr(bill.DueAmount)}.");
        }

        if (amount <= 0 || amount > account.DepositBalance)
        {
            return Error.Rule("billing.deposit_insufficient", $"The advance balance is ₹{Money.FormatInr(Math.Max(account.DepositBalance, 0m))}.");
        }

        var receipt = await NewReceiptAsync(bill.FacilityId, ReceiptPurpose.BillPayment, bill.PatientId, ReceiptSource.DepositAdjustment, null, ct);
        receipt.BillId = bill.Id;
        receipt.VisitId = bill.VisitId;
        receipt.AdmissionId = bill.AdmissionId;
        receipt.IpAccountId = account.Id;
        receipt.AddPayment(new PaymentLineData(PaymentMode.Advance, amount, Reference: $"Advance of admission {account.AdmissionNumber}"));
        receipt.Record(time.GetUtcNow());
        account.AdjustDeposit(amount);
        bill.ApplyPayment(amount, allowExcess: false);
        db.Add(receipt);
        return receipt;
    }

    /// <summary>
    /// Receipts money a patient paid online before any bill exists (appointment prepayment) as a patient advance. The
    /// caller allocates inside its transaction and saves; idempotency is the caller's (one advance per gateway payment).
    /// </summary>
    internal async Task<Receipt> RecordPatientAdvanceAsync(
        Guid facilityId, Guid patientId, decimal amount, string gatewayPaymentId, string? gatewayOrderId, string notes, DateTimeOffset receivedAt, CancellationToken ct)
    {
        var receipt = await NewReceiptAsync(facilityId, ReceiptPurpose.Advance, patientId, ReceiptSource.PaymentGateway, null, ct);
        receipt.ReceivedByName = "Online payment";
        receipt.Notes = Truncate(notes, 500);
        receipt.AddPayment(new PaymentLineData(PaymentMode.PaymentGateway, amount, Reference: Truncate(gatewayOrderId, 64), ExternalId: gatewayPaymentId));
        receipt.Record(receivedAt);
        db.Add(receipt);
        return receipt;
    }

    /// <summary>
    /// Adjusts part of a patient advance (online prepayment) against an OP bill: a journal, no money moves. Mirrors the IP
    /// advance adjustment; the receipt carries the advance so a cancelled bill can give it back. The caller saves.
    /// </summary>
    internal async Task<Result<Receipt>> AdjustPatientAdvanceAsync(Bill bill, PatientAdvance advance, decimal amount, CancellationToken ct)
    {
        if (!bill.IsOpenForPayment || amount > bill.DueAmount)
        {
            return Error.Rule("billing.overpayment", $"The balance on this bill is ₹{Money.FormatInr(bill.DueAmount)}.");
        }

        if (advance.PatientId != bill.PatientId)
        {
            return Error.Rule("billing.advance_other_patient", "This advance belongs to another patient.");
        }

        var adjusted = advance.Adjust(amount);
        if (adjusted.IsFailure)
        {
            return adjusted.Error!;
        }

        var receipt = await NewReceiptAsync(bill.FacilityId, ReceiptPurpose.BillPayment, bill.PatientId, ReceiptSource.DepositAdjustment, null, ct);
        receipt.BillId = bill.Id;
        receipt.VisitId = bill.VisitId;
        receipt.AdvanceId = advance.Id;
        receipt.AddPayment(new PaymentLineData(PaymentMode.Advance, amount, Reference: $"Advance {advance.ReceiptNumber}"));
        receipt.Record(time.GetUtcNow());
        bill.ApplyPayment(amount, allowExcess: false);
        db.Add(receipt);
        return receipt;
    }

    /// <summary>Insurer/TPA/corporate settlement received against a claim (receipt mode Insurance). The caller saves.</summary>
    internal async Task<Receipt> RecordPayerSettlementAsync(Bill bill, InsuranceClaim claim, decimal amount, string? utrNumber, CancellationToken ct)
    {
        var receipt = await NewReceiptAsync(bill.FacilityId, ReceiptPurpose.PayerSettlement, bill.PatientId, ReceiptSource.InsuranceDesk, null, ct);
        receipt.BillId = bill.Id;
        receipt.VisitId = bill.VisitId;
        receipt.AdmissionId = bill.AdmissionId;
        receipt.IpAccountId = bill.IpAccountId;
        receipt.PayerId = claim.PayerId;
        receipt.ClaimId = claim.Id;
        receipt.AddPayment(new PaymentLineData(PaymentMode.Insurance, amount, Reference: utrNumber));
        receipt.Record(time.GetUtcNow());
        db.Add(receipt);
        return receipt;
    }

    private Result<List<PaymentLineData>> ValidateCounterLines(IReadOnlyList<PaymentLineInput> inputs)
    {
        var today = BillingTime.Today(time);
        var lines = inputs.Select(i => i.ToData()).ToList();
        foreach (var line in lines)
        {
            if (PaymentLineRules.Validate(line, today, atCounter: true) is { } error)
            {
                return error;
            }
        }

        return lines;
    }

    private async Task<Receipt> NewReceiptAsync(Guid facilityId, ReceiptPurpose purpose, Guid patientId, ReceiptSource source, CashierShift? shift, CancellationToken ct) => new()
    {
        FacilityId = facilityId,
        ReceiptNumber = await numbers.NextAsync(BillingNumberSeries.Receipt, facilityId, ct),
        Purpose = purpose,
        PatientId = patientId,
        Source = source,
        Counter = shift?.Counter,
        ShiftId = shift?.Id,
        ReceivedBy = currentUser.UserId,
        ReceivedByName = currentUser.DisplayName ?? currentUser.UserName,
    };

    /// <param name="bill">The bill the payment is for, already updated with it: once it is settled, it goes on WhatsApp.</param>
    private async Task QueueReceiptMessageAsync(Receipt receipt, string? documentNumber, Bill? bill, CancellationToken ct)
    {
        if (receipt.Payments.All(p => p.Mode == PaymentMode.Advance))
        {
            return;
        }

        var patient = await db.Set<Patient>().IgnoreQueryFilters()
            .Where(p => p.Id == receipt.PatientId)
            .Select(p => new { p.Phone, p.Email, p.FullName })
            .FirstOrDefaultAsync(ct);
        if (patient?.Phone is null)
        {
            return;
        }

        notifications.Queue(
            "bill.receipt",
            new NotificationRecipient(patient.Phone, patient.Email, receipt.PatientId, patient.FullName),
            new Dictionary<string, string>
            {
                ["amount"] = Money.FormatInr(receipt.Amount),
                ["billNumber"] = documentNumber ?? receipt.ReceiptNumber,
                ["receiptNumber"] = receipt.ReceiptNumber,
            },
            [NotificationChannel.Sms],
            TimeSpan.FromDays(1));

        if (bill is not null)
        {
            await bills.QueueOnPaymentAsync(bill, ct);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
