namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum PaymentMode
{
    Cash = 0,
    Card,
    Upi,
    NetBanking,
    Cheque,
    PaymentGateway,

    /// <summary>Settlement received from an insurer/TPA/corporate against a claim.</summary>
    Insurance,

    Wallet,

    /// <summary>An advance (IP deposit or patient advance) adjusted against a bill — no money changes hands.</summary>
    Advance,
}

public enum ReceiptPurpose
{
    BillPayment = 0,

    /// <summary>In-patient advance/deposit (IPB-01).</summary>
    Deposit,

    PayerSettlement,

    /// <summary>Patient advance received before any bill exists (online appointment prepayment, OA-03).</summary>
    Advance,
}

public enum ReceiptSource
{
    Counter = 0,
    PosTerminal,
    PaymentGateway,
    InsuranceDesk,
    DepositAdjustment,
}

/// <summary>
/// Money received (MB-04): one receipt can combine several modes (split payment). Receipts are never edited; mistakes
/// are corrected with refunds or credit notes so the day-end cash and the Tally day book always reconcile.
/// </summary>
public sealed class Receipt : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public ReceiptPurpose Purpose { get; set; }
    public Guid? BillId { get; set; }
    public Guid? IpAccountId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? VisitId { get; set; }

    /// <summary>Insurer/TPA/corporate for payer settlements.</summary>
    public Guid? PayerId { get; set; }

    public Guid? ClaimId { get; set; }

    /// <summary>The patient advance this adjustment (mode Advance on an OP bill) drew on, or the advance this receipt created.</summary>
    public Guid? AdvanceId { get; set; }

    public decimal Amount { get; set; }
    public ReceiptSource Source { get; set; }
    public string? Counter { get; set; }
    public Guid? ReceivedBy { get; set; }
    public string? ReceivedByName { get; set; }

    /// <summary>Cashier shift the money was collected in (day-end cash closing).</summary>
    public Guid? ShiftId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
    public string? Notes { get; set; }
    public List<ReceiptPayment> Payments { get; set; } = [];

    /// <summary>"Cash", "Card + Upi"… (single mode name, or modes joined for split payments).</summary>
    public string ModeSummary => string.Join(" + ", Payments.Select(p => p.Mode).Distinct().Select(m => m.ToString()));

    public void AddPayment(PaymentLineData line) => Payments.Add(new ReceiptPayment
    {
        ReceiptId = Id,
        Mode = line.Mode,
        Amount = Money.Round(line.Amount),
        Reference = Clean(line.Reference),
        CardLast4 = Clean(line.CardLast4),
        CardNetwork = Clean(line.CardNetwork),
        ChequeNumber = Clean(line.ChequeNumber),
        ChequeBank = Clean(line.ChequeBank),
        ChequeDate = line.ChequeDate,
        ExternalId = Clean(line.ExternalId),
    });

    /// <summary>
    /// Seals the receipt: totals the payment lines and, for real money from the patient, raises PaymentReceived so
    /// other modules (for example diagnostics waiting for payment) react in the same transaction.
    /// </summary>
    public Result Record(DateTimeOffset now)
    {
        if (Payments.Count == 0 || Payments.Any(p => p.Amount <= 0))
        {
            return Error.Field("payments", "Add at least one payment with an amount greater than zero.");
        }

        Amount = Payments.Sum(p => p.Amount);
        ReceivedAt = now;

        var isPatientMoney = Purpose is ReceiptPurpose.BillPayment or ReceiptPurpose.Deposit or ReceiptPurpose.Advance
                             && Payments.Any(p => p.Mode != PaymentMode.Advance);
        if (isPatientMoney)
        {
            Raise(new PaymentReceived(Id, ReceiptNumber, PatientId, BillId, VisitId, AdmissionId, Amount, ModeSummary, now, FacilityId));
        }

        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One mode of a (split) payment with the evidence needed to reconcile it.</summary>
public sealed class ReceiptPayment : Entity
{
    public Guid ReceiptId { get; set; }
    public PaymentMode Mode { get; set; }
    public decimal Amount { get; set; }

    /// <summary>UPI UTR, net-banking/wallet transaction id, POS RRN or insurer UTR.</summary>
    public string? Reference { get; set; }

    public string? CardLast4 { get; set; }
    public string? CardNetwork { get; set; }
    public string? ChequeNumber { get; set; }
    public string? ChequeBank { get; set; }
    public DateOnly? ChequeDate { get; set; }

    /// <summary>POS terminal transaction id or gateway payment id; unique per mode, which makes re-recording impossible.</summary>
    public string? ExternalId { get; set; }
}

public sealed record PaymentLineData(
    PaymentMode Mode,
    decimal Amount,
    string? Reference = null,
    string? CardLast4 = null,
    string? CardNetwork = null,
    string? ChequeNumber = null,
    string? ChequeBank = null,
    DateOnly? ChequeDate = null,
    string? ExternalId = null);

/// <summary>Mode-specific evidence a cashier must capture so every non-cash payment can be reconciled with the bank.</summary>
public static class PaymentLineRules
{
    /// <summary>Indian cheques are valid for three months from their date.</summary>
    public const int ChequeValidityMonths = 3;

    public static Error? Validate(PaymentLineData line, DateOnly today, bool atCounter)
    {
        if (line.Amount <= 0)
        {
            return Error.Field("payments", "Each payment amount must be greater than zero.");
        }

        if (Money.Round(line.Amount) != line.Amount)
        {
            return Error.Field("payments", "Amounts can have at most 2 decimal places.");
        }

        switch (line.Mode)
        {
            case PaymentMode.Advance or PaymentMode.Insurance when atCounter:
                return Error.Field("payments", "Advance adjustments and insurance settlements are recorded from the IP account and the claim.");

            case PaymentMode.Card when line.ExternalId is null && !IsLast4(line.CardLast4):
                return Error.Field("payments", "Enter the last 4 digits of the card.");

            case PaymentMode.Upi or PaymentMode.NetBanking or PaymentMode.Wallet or PaymentMode.PaymentGateway or PaymentMode.Insurance
                when string.IsNullOrWhiteSpace(line.Reference) && string.IsNullOrWhiteSpace(line.ExternalId):
                return Error.Field("payments", "Enter the transaction reference (UTR).");

            case PaymentMode.Cheque:
                if (line.ChequeNumber is not { Length: 6 } number || !number.All(char.IsAsciiDigit))
                {
                    return Error.Field("payments", "Enter the 6-digit cheque number.");
                }

                if (string.IsNullOrWhiteSpace(line.ChequeBank))
                {
                    return Error.Field("payments", "Enter the bank the cheque is drawn on.");
                }

                if (line.ChequeDate is not { } date)
                {
                    return Error.Field("payments", "Enter the cheque date.");
                }

                if (date > today)
                {
                    return Error.Field("payments", "Post-dated cheques are not accepted.");
                }

                if (date < today.AddMonths(-ChequeValidityMonths))
                {
                    return Error.Field("payments", "This cheque is older than 3 months and no longer valid.");
                }

                break;
        }

        return null;
    }

    public static bool IsLast4(string? value) => value is { Length: 4 } && value.All(char.IsAsciiDigit);
}
