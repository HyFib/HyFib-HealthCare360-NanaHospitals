using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>How one settled transaction compares with HC360's records.</summary>
public enum SettlementMatch
{
    /// <summary>A receipt or refund with the same gateway id and the same amount.</summary>
    Matched = 0,

    /// <summary>The receipt or refund exists, but HC360 records a different amount.</summary>
    AmountDiffers,

    /// <summary>The gateway took this money, but no receipt records it.</summary>
    NoReceipt,

    /// <summary>The gateway returned this money, but no HC360 refund records it.</summary>
    NoRefund,

    /// <summary>An adjustment or transfer by the gateway: there is no receipt to match; the accountant reviews it.</summary>
    NotApplicable,
}

/// <summary>
/// A payout from the payment gateway to the hospital's bank (INT-PG reconciliation), as the gateway reported it. It is
/// stored once per provider and settlement id. Facilities that share a merchant account share its settlements: the
/// settlement is kept under the facility whose account it was read with first, and each line records the facility its
/// money belongs to, so every facility's day book clears its own receipts. Card and bank details are never stored.
/// </summary>
[NotAudited]
public sealed class GatewaySettlement : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string SettlementId { get; set; } = string.Empty;

    /// <summary>The bank's reference for the credit, to find it on the bank statement.</summary>
    public string? Utr { get; set; }

    public PaymentSettlementStatus Status { get; set; }

    /// <summary>What reached the bank.</summary>
    public decimal Amount { get; set; }

    /// <summary>Payments in the settlement, before charges.</summary>
    public decimal PaymentsAmount { get; set; }

    public decimal RefundsAmount { get; set; }

    /// <summary>Adjustments, transfers and other corrections, before charges (negative when the gateway took money back).</summary>
    public decimal AdjustmentsAmount { get; set; }

    /// <summary>The gateway's charges, GST included (on the transactions and on the settlement itself).</summary>
    public decimal Fees { get; set; }

    /// <summary>The GST part of <see cref="Fees"/> (input GST for the hospital).</summary>
    public decimal Tax { get; set; }

    /// <summary>Charges on the settlement itself, such as an instant-settlement fee, GST included (part of <see cref="Fees"/>).</summary>
    public decimal SettlementFees { get; set; }

    public decimal SettlementTax { get; set; }
    public DateTimeOffset SettledAt { get; set; }

    /// <summary>The Indian calendar day of <see cref="SettledAt"/>: the date of the settlement journal.</summary>
    public DateOnly SettledOn { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }

    /// <summary>
    /// The exceptions of every line were raised and saved. Until then (a run that stopped or clashed with another) the
    /// next fetch of the day raises them again; lines that still do not match are raised again on every fetch anyway.
    /// </summary>
    public bool ExceptionsRaised { get; set; }

    public List<GatewaySettlementLine> Lines { get; set; } = [];

    /// <summary>
    /// What reached the bank less what the transactions add up to. Zero when the settlement is consistent; anything else
    /// stays in the clearing ledger and is listed as an exception.
    /// </summary>
    public decimal Difference => Amount - (PaymentsAmount - RefundsAmount + AdjustmentsAmount - Fees);

    /// <summary>
    /// The settlement as the gateway reported it, every line kept under <paramref name="facilityId"/> until it is
    /// matched. Charges reported on the settlement itself count only when they explain the payout (the transactions less
    /// those charges equal the amount paid), because some gateways repeat the transactions' charges there.
    /// </summary>
    public static GatewaySettlement From(Guid facilityId, string provider, PaymentSettlement reported, DateOnly settledOn, DateTimeOffset now)
    {
        var settlement = new GatewaySettlement
        {
            FacilityId = facilityId,
            Provider = provider,
            SettlementId = reported.SettlementId,
            Utr = reported.Utr,
            Status = reported.Status,
            Amount = Money.Round(reported.Amount),
            SettledAt = reported.SettledAt,
            SettledOn = settledOn,
            FetchedAt = now,
        };

        foreach (var item in reported.Items)
        {
            settlement.Lines.Add(new GatewaySettlementLine
            {
                GatewaySettlementId = settlement.Id,
                FacilityId = facilityId,
                Type = item.Type,
                EntityId = item.EntityId,
                PaymentId = item.PaymentId,
                OrderId = item.OrderId,
                Amount = Money.Round(item.Amount),
                Fee = Money.Round(item.Fee),
                Tax = Money.Round(item.Tax),
                Credit = Money.Round(item.Credit),
                Debit = Money.Round(item.Debit),
                Method = item.Method,
                TransactionAt = item.CreatedAt,
                SettledAt = reported.SettledAt,
                Match = item.Type is PaymentTransactionType.Payment ? SettlementMatch.NoReceipt
                    : item.Type is PaymentTransactionType.Refund ? SettlementMatch.NoRefund
                    : SettlementMatch.NotApplicable,
            });
        }

        var linesNet = settlement.Lines.Sum(l => l.Net);
        var settlementFees = Money.Round(reported.Fees);
        var settlementLevel = settlementFees > 0 && settlement.Amount == linesNet - settlementFees;
        settlement.SettlementFees = settlementLevel ? settlementFees : 0m;
        settlement.SettlementTax = settlementLevel ? Money.Round(reported.Tax) : 0m;
        settlement.PaymentsAmount = settlement.Lines.Where(l => l.Type == PaymentTransactionType.Payment).Sum(l => l.Gross);
        settlement.RefundsAmount = -settlement.Lines.Where(l => l.Type == PaymentTransactionType.Refund).Sum(l => l.Gross);
        settlement.AdjustmentsAmount = settlement.Lines.Where(l => l.Type is not (PaymentTransactionType.Payment or PaymentTransactionType.Refund)).Sum(l => l.Gross);
        settlement.Fees = settlement.Lines.Sum(l => l.Fee) + settlement.SettlementFees;
        settlement.Tax = settlement.Lines.Sum(l => l.Tax) + settlement.SettlementTax;
        return settlement;
    }

    /// <summary>A later report of the same settlement (created → processed, or failed). Returns true when it changed.</summary>
    public bool UpdateStatus(PaymentSettlementStatus status, string? utr, DateTimeOffset now)
    {
        if (status == Status && (utr is null || utr == Utr))
        {
            return false;
        }

        Status = status;
        Utr = utr ?? Utr;
        StatusChangedAt = now;
        return true;
    }
}

/// <summary>One transaction of a settlement and what it matched in HC360.</summary>
[NotAudited]
public sealed class GatewaySettlementLine : Entity
{
    public Guid GatewaySettlementId { get; set; }

    /// <summary>The facility the money belongs to: its receipt's or refund's, else its order's or booking's, else the settlement's.</summary>
    public Guid FacilityId { get; set; }

    public PaymentTransactionType Type { get; set; }

    /// <summary>The gateway's id of the transaction (<c>pay_…</c>, <c>rfnd_…</c>, <c>adj_…</c>).</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>For a refund, the payment it returned money from.</summary>
    public string? PaymentId { get; set; }

    public string? OrderId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>The gateway's charge on this transaction, GST included.</summary>
    public decimal Fee { get; set; }

    public decimal Tax { get; set; }
    public decimal Credit { get; set; }
    public decimal Debit { get; set; }
    public string? Method { get; set; }

    /// <summary>When the payment was taken or the refund made at the gateway.</summary>
    public DateTimeOffset? TransactionAt { get; set; }

    public DateTimeOffset SettledAt { get; set; }
    public SettlementMatch Match { get; set; }
    public Guid? ReceiptId { get; set; }
    public Guid? RefundId { get; set; }

    /// <summary>What HC360 recorded for the matched receipt or refund.</summary>
    public decimal? RecordedAmount { get; set; }

    /// <summary>What the transaction added to the payout.</summary>
    public decimal Net => Credit - Debit;

    /// <summary>The transaction before charges: positive for money in, negative for money returned.</summary>
    public decimal Gross => Credit - Debit + Fee;

    /// <summary>Compares the line with the receipt (a payment) or refund (a refund) that carries its gateway id.</summary>
    public void MatchTo(Guid? receiptId, Guid? refundId, decimal recordedAmount, Guid facilityId)
    {
        ReceiptId = receiptId;
        RefundId = refundId;
        RecordedAmount = recordedAmount;
        FacilityId = facilityId;
        Match = Money.Round(recordedAmount) == Amount ? SettlementMatch.Matched : SettlementMatch.AmountDiffers;
    }

    /// <summary>Nothing in HC360 carries the line's id; <paramref name="facilityId"/> is its order's or booking's facility when known.</summary>
    public void Unmatched(Guid? facilityId)
    {
        ReceiptId = null;
        RefundId = null;
        RecordedAmount = null;
        if (facilityId is { } facility)
        {
            FacilityId = facility;
        }

        Match = Type switch
        {
            PaymentTransactionType.Payment => SettlementMatch.NoReceipt,
            PaymentTransactionType.Refund => SettlementMatch.NoRefund,
            _ => SettlementMatch.NotApplicable,
        };
    }
}

/// <summary>One facility's part of a processed settlement, for its day book.</summary>
public sealed record SettlementPortion(Guid FacilityId, bool IsOwner, decimal Net, decimal Charges, decimal Tax)
{
    /// <summary>The gross amount the settlement clears from the payment-gateway clearing ledger.</summary>
    public decimal Gross => Net + Charges + Tax;
}

/// <summary>
/// Splits a settlement between the facilities its transactions belong to. Each facility gets the payout, charges and
/// GST of its own transactions; the facility that holds the settlement also gets any settlement-level charge and any
/// unexplained difference, so the portions always add up to what the bank received.
/// </summary>
public static class SettlementJournal
{
    public static IReadOnlyList<SettlementPortion> Portions(GatewaySettlement settlement)
    {
        var byFacility = settlement.Lines
            .GroupBy(l => l.FacilityId)
            .ToDictionary(g => g.Key, g => (Net: g.Sum(l => l.Net), Charges: g.Sum(l => l.Fee - l.Tax), Tax: g.Sum(l => l.Tax)));
        var others = byFacility.Where(kv => kv.Key != settlement.FacilityId)
            .Select(kv => new SettlementPortion(kv.Key, false, kv.Value.Net, kv.Value.Charges, kv.Value.Tax))
            .ToList();
        var own = byFacility.GetValueOrDefault(settlement.FacilityId);
        var owner = new SettlementPortion(
            settlement.FacilityId,
            true,
            settlement.Amount - others.Sum(p => p.Net),
            own.Charges + settlement.SettlementFees - settlement.SettlementTax,
            own.Tax + settlement.SettlementTax);
        return [owner, .. others.OrderBy(p => p.FacilityId)];
    }

    /// <summary>The settlement journal's voucher number: the settlement id, with a facility suffix for a shared account's other facilities.</summary>
    public static string VoucherNumber(GatewaySettlement settlement, SettlementPortion portion) =>
        portion.IsOwner ? settlement.SettlementId : $"{settlement.SettlementId}-{portion.FacilityId.ToString("N")[..6]}";
}

/// <summary>
/// How often the reconciliation job asks the gateway about something still open: every run (5 minutes) for the first
/// two hours, then about hourly for two days, then about every six hours until <see cref="Window"/>. A webhook normally
/// arrives first; this catches the ones that never come.
/// </summary>
public static class GatewayPolling
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>Let the patient's own checkout and the webhook confirm a fresh payment first.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    public static bool IsDue(DateTimeOffset since, DateTimeOffset now)
    {
        var age = now - since;
        if (age < Grace || age >= Window)
        {
            return false;
        }

        if (age < TimeSpan.FromHours(2))
        {
            return true;
        }

        var firstRunOfHour = now.Minute < Interval.TotalMinutes;
        return age < TimeSpan.FromDays(2) ? firstRunOfHour : firstRunOfHour && now.Hour % 6 == 0;
    }

    /// <summary>
    /// The same schedule as <see cref="IsDue"/>, as the windows of start times due at <paramref name="now"/> (each
    /// after its first time and up to its second), freshest first: a query asks for each window's newest records, so a
    /// fresh payment is never crowded out by older ones.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset After, DateTimeOffset Until)> DueWindows(DateTimeOffset now)
    {
        List<(DateTimeOffset After, DateTimeOffset Until)> windows = [(now - TimeSpan.FromHours(2), now - Grace)];
        if (IsHourly(now))
        {
            windows.Add((now - TimeSpan.FromDays(2), now - TimeSpan.FromHours(2)));
            if (now.Hour % 6 == 0)
            {
                windows.Add((now - Window, now - TimeSpan.FromDays(2)));
            }
        }

        return windows;
    }

    /// <summary>About hourly: refunds take days, settlements arrive once a day.</summary>
    public static bool IsHourly(DateTimeOffset now) => now.Minute < Interval.TotalMinutes;
}

public enum DiscrepancyKind
{
    /// <summary>The gateway took the patient's money, but no receipt records it: the patient was not credited.</summary>
    UnrecordedPayment = 0,

    /// <summary>A receipt for an online payment that no settlement has paid to the bank after the expected days.</summary>
    UnsettledReceipt,

    /// <summary>The gateway and HC360 record different amounts, or the gateway adjusted a settlement.</summary>
    AmountMismatch,

    /// <summary>The gateway reports a refund differently from HC360: unknown to HC360, a different amount, or failed.</summary>
    RefundMismatch,
}

public enum DiscrepancyStatus
{
    Open = 0,
    Resolved,
}

/// <summary>
/// A difference between the payment gateway and HC360 that someone must look at (shown to accountants as an
/// exception). Found by the settlement match, the payment re-check and the refund follow-up; kept once per kind and
/// gateway id (<see cref="Key"/>). It clears itself when the cause goes away (the receipt is recorded, the payment is
/// settled, the refund is processed); otherwise an accountant resolves it with a note, which is audited.
/// </summary>
public sealed class GatewayDiscrepancy : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public DiscrepancyKind Kind { get; set; }

    /// <summary>1 = worst: lists sort by it, then by amount.</summary>
    public int Rank { get; set; }

    /// <summary>The gateway id it is about (payment, refund, adjustment or settlement); unique per kind.</summary>
    public string Key { get; set; } = string.Empty;

    public DiscrepancyStatus Status { get; set; }

    /// <summary>The gateway order (or payment link) the money was for, when known.</summary>
    public string? OrderId { get; set; }

    public Guid? GatewayOrderRecordId { get; set; }
    public Guid? AppointmentId { get; set; }
    public Guid? BillId { get; set; }
    public Guid? IpAccountId { get; set; }
    public Guid? ReceiptId { get; set; }
    public Guid? RefundId { get; set; }
    public Guid? SettlementLineId { get; set; }

    /// <summary>What HC360 records.</summary>
    public decimal? RecordedAmount { get; set; }

    /// <summary>What the gateway reports.</summary>
    public decimal? GatewayAmount { get; set; }

    /// <summary>When the gateway took the payment or made the refund, when known.</summary>
    public DateTimeOffset? GatewayAt { get; set; }

    /// <summary>What is wrong, in plain words (document numbers and gateway ids only, never a patient's name).</summary>
    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public string? ResolvedByName { get; set; }
    public string? Resolution { get; set; }

    /// <summary>Cleared by HC360 itself because the cause went away, not by a person.</summary>
    public bool ResolvedAutomatically { get; set; }

    public static int RankOf(DiscrepancyKind kind) => kind switch
    {
        DiscrepancyKind.UnrecordedPayment => 1,
        DiscrepancyKind.UnsettledReceipt => 2,
        DiscrepancyKind.AmountMismatch => 3,
        _ => 4,
    };

    public static GatewayDiscrepancy Open(Guid facilityId, DiscrepancyKind kind, string key, string summary, DateTimeOffset now) => new()
    {
        FacilityId = facilityId,
        Kind = kind,
        Rank = RankOf(kind),
        Key = key,
        Status = DiscrepancyStatus.Open,
        Summary = Clip(summary, 300)!,
        DetectedAt = now,
        LastSeenAt = now,
    };

    /// <summary>
    /// The cause was seen again. An exception HC360 cleared by itself opens again; one a person resolved stays resolved,
    /// because they have already dealt with it.
    /// </summary>
    public void SeenAgain(string summary, DateTimeOffset now)
    {
        if (Status == DiscrepancyStatus.Resolved && !ResolvedAutomatically)
        {
            return;
        }

        if (Status == DiscrepancyStatus.Resolved)
        {
            Status = DiscrepancyStatus.Open;
            ResolvedAt = null;
            ResolvedBy = null;
            ResolvedByName = null;
            Resolution = null;
            ResolvedAutomatically = false;
        }

        Summary = Clip(summary, 300)!;
        LastSeenAt = now;
    }

    /// <summary>An accountant closes the exception, saying what they did about it.</summary>
    public Result Resolve(Guid userId, string? userName, string note, DateTimeOffset now)
    {
        if (Status != DiscrepancyStatus.Open)
        {
            return Error.Rule("billing.discrepancy_closed", "This exception is already resolved.");
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Say what was done about it.");
        }

        Status = DiscrepancyStatus.Resolved;
        ResolvedAt = now;
        ResolvedBy = userId;
        ResolvedByName = Clip(userName, 200);
        Resolution = Clip(note.Trim(), 500);
        ResolvedAutomatically = false;
        return Result.Success();
    }

    /// <summary>The cause went away (for example the receipt was recorded). Returns true when an open exception was closed.</summary>
    public bool Clear(string why, DateTimeOffset now, Guid? userId = null, string? userName = null)
    {
        if (Status != DiscrepancyStatus.Open)
        {
            return false;
        }

        Status = DiscrepancyStatus.Resolved;
        ResolvedAt = now;
        ResolvedBy = userId;
        ResolvedByName = Clip(userName, 200);
        Resolution = Clip(why, 500);
        ResolvedAutomatically = userId is null;
        return true;
    }

    /// <summary>
    /// A person's action removed the cause (they recorded the receipt, say). The exception is closed in their name,
    /// also when HC360 had already cleared it while the action ran.
    /// </summary>
    public void HandledBy(Guid? userId, string? userName, string note, DateTimeOffset now)
    {
        if (Status == DiscrepancyStatus.Open)
        {
            Clear(note, now, userId, userName);
        }
        else if (ResolvedAutomatically && userId is not null)
        {
            ResolvedBy = userId;
            ResolvedByName = Clip(userName, 200);
            Resolution = Clip(note, 500);
            ResolvedAutomatically = false;
        }
    }

    private static string? Clip(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}

public enum WebhookEventOutcome
{
    /// <summary>Being handled (or the handling stopped part way, so a redelivery is handled again).</summary>
    Received = 0,

    /// <summary>It changed something: a receipt was recorded, a booking confirmed, a refund updated.</summary>
    Processed,

    /// <summary>Valid, but nothing of HC360's to act on.</summary>
    Ignored,

    /// <summary>It could not be handled (for example the gateway could not be asked); the job checks again later.</summary>
    Failed,
}

/// <summary>
/// One verified webhook delivery from the payment gateway, kept by its event id so a redelivered event is not handled
/// twice. Only the event type and the gateway ids are kept: never the payload, which carries the patient's contact
/// details and card or bank information. Deleted with the integration log after its retention period.
/// </summary>
[NotAudited]
public sealed class GatewayWebhookEvent : Entity
{
    public string Provider { get; set; } = string.Empty;

    /// <summary>The gateway's event id (<c>X-Razorpay-Event-Id</c>), else a SHA-256 of the body.</summary>
    public string EventId { get; set; } = string.Empty;

    public string? EventType { get; set; }
    public string? OrderId { get; set; }
    public string? PaymentId { get; set; }
    public string? RefundId { get; set; }
    public string? SettlementId { get; set; }
    public Guid? FacilityId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// When the latest delivery started being handled: one still <see cref="WebhookEventOutcome.Received"/> after a
    /// while stopped part way.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? HandledAt { get; set; }
    public WebhookEventOutcome Outcome { get; set; }

    /// <summary>How many times the gateway delivered it.</summary>
    public int Deliveries { get; set; }

    public string? Note { get; set; }
}
