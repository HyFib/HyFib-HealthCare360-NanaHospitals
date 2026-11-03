using System.Globalization;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>What a line of the acquirer's settlement file is.</summary>
public enum PosSettlementLineType
{
    /// <summary>A card or UPI payment the acquirer paid to the bank.</summary>
    Sale = 0,

    /// <summary>A refund, void or reversal the acquirer took back from a payout.</summary>
    Refund,

    /// <summary>A chargeback, dispute or other correction by the acquirer.</summary>
    Adjustment,
}

/// <summary>How a settled line compares with HC360's records.</summary>
public enum PosSettlementMatch
{
    /// <summary>A card or UPI receipt (or, for a refund, a refund paid by card or UPI) with the same amount.</summary>
    Matched = 0,

    /// <summary>The receipt was found by its RRN, but HC360 records a different amount.</summary>
    AmountDiffers,

    /// <summary>The acquirer paid this money, but no receipt records it.</summary>
    NoReceipt,

    /// <summary>The acquirer took this money back as a refund, but no HC360 refund records it.</summary>
    NoRefund,

    /// <summary>A chargeback or adjustment: there is nothing to match, and an accountant looks at it.</summary>
    Review,

    /// <summary>
    /// Taken at a terminal of another counter (setting <c>otherTerminals</c>) that no payment of HC360's pharmacy or
    /// canteen names: a counter HC360 does not record, or a sale typed without its reference. Nothing to match; the money
    /// still clears through the settlement journal.
    /// </summary>
    OtherCounter,

    /// <summary>
    /// Its report was withdrawn as imported in error: it matches nothing, its journal is reversed on the day of the
    /// withdrawal, and a correct report may bring the transaction again.
    /// </summary>
    Withdrawn,
}

/// <summary>What a matched line was found by, from the surest to the weakest.</summary>
public enum PosMatchBasis
{
    /// <summary>The RRN (UPI: the UTR) the terminal returned, or that the cashier typed as the reference.</summary>
    Rrn = 0,

    /// <summary>The approval code and the amount, within two days.</summary>
    ApprovalCode,

    /// <summary>The card's last four digits and the amount, within a day: a card payment typed without a reference.</summary>
    CardAndAmount,
}

/// <summary>
/// Card and UPI settlement reconciliation settings (<c>billing.pos.settlements</c>, per facility).
/// </summary>
public sealed record PosSettlementSettings
{
    public const string Key = "billing.pos.settlements";

    /// <summary>A card or UPI receipt the acquirer has not settled after this many days is listed (cards settle the next working day).</summary>
    public int SettlementDays { get; init; } = 3;

    /// <summary>
    /// The acquirer's terminal ids (TIDs) of counters that HC360's billing does not record, such as the pharmacy's: their
    /// lines are not matched with receipts.
    /// </summary>
    public IReadOnlyList<string> OtherTerminals { get; init; } = [];

    /// <summary>
    /// Column headings of the hospital's acquirer, by field (<c>rrn</c>, <c>approvalCode</c>, <c>amount</c>, …), tried
    /// before the built-in ones. See <see cref="PosSettlementFile.Fields"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Columns { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public int EffectiveSettlementDays => Math.Clamp(SettlementDays, 1, 30);

    public bool IsOtherTerminal(string? terminalId) =>
        terminalId is not null && OtherTerminals.Any(t => string.Equals(t?.Trim(), terminalId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// An acquirer's settlement file as an accountant imported it (INT-POS reconciliation): what it held and what was new.
/// The file itself is not kept; a file already imported (same content) is refused.
/// </summary>
[NotAudited]
public sealed class PosSettlementImport : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>The acquirer or bank the file came from, as the accountant named it ("HDFC Bank", "Pine Labs").</summary>
    public string Acquirer { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public string FileSha256 { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    /// <summary>Transaction rows read from the file.</summary>
    public int Rows { get; set; }

    /// <summary>Lines stored for the first time.</summary>
    public int Added { get; set; }

    /// <summary>Lines another file had already brought (acquirers' reports overlap).</summary>
    public int AlreadyImported { get; set; }

    /// <summary>Rows left out: without an amount, a reference or a settlement date, or repeated (totals are not counted).</summary>
    public int Ignored { get; set; }

    public DateOnly? FirstSettledOn { get; set; }
    public DateOnly? LastSettledOn { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public Guid? ImportedBy { get; set; }
    public string? ImportedByName { get; set; }

    /// <summary>
    /// The Indian calendar day the report was withdrawn as imported in error (asked for by one person, approved by
    /// another): its transactions' journals are reversed in that day's books. Null while it stands.
    /// </summary>
    public DateOnly? WithdrawnOn { get; set; }

    public DateTimeOffset? WithdrawnAt { get; set; }

    /// <summary>Who approved the withdrawal; who asked for it is in its approval.</summary>
    public string? WithdrawnByName { get; set; }

    public string? WithdrawReason { get; set; }

    /// <summary>Withdraws the report and its own transactions (those it brought first), in the books of <paramref name="on"/>.</summary>
    public void Withdraw(IEnumerable<PosSettlementLine> lines, DateOnly on, DateTimeOffset at, string? approvedBy, string reason)
    {
        foreach (var line in lines.Where(l => l.ImportId == Id))
        {
            line.Withdraw(on);
        }

        (WithdrawnOn, WithdrawnAt, WithdrawnByName, WithdrawReason) = (on, at, approvedBy, reason);
    }
}

/// <summary>
/// One transaction of an acquirer's settlement, and what it matched in HC360. Only the card's last four digits are kept,
/// never the card number or the customer's details. <see cref="Net"/> is what the transaction added to the payout;
/// <see cref="Charges"/> (the acquirer's MDR without GST) and <see cref="Tax"/> (GST on it) make up the rest of
/// <see cref="Gross"/>, so <c>Gross = Net + Charges + Tax</c> always holds and the settlement journal balances.
/// </summary>
[NotAudited]
public sealed class PosSettlementLine : Entity, IFacilityScoped, IVersioned
{
    public Guid FacilityId { get; set; }
    public Guid ImportId { get; set; }

    /// <summary>
    /// Identifies the transaction however often it is imported: its type and RRN, or without an RRN its terminal,
    /// approval code, amount and day (<see cref="PosSettlementRow.Key"/>). Unique per facility.
    /// </summary>
    public string LineKey { get; set; } = string.Empty;

    public string Acquirer { get; set; } = string.Empty;
    public PosSettlementLineType Type { get; set; }

    /// <summary><see cref="PaymentMode.Card"/> or <see cref="PaymentMode.Upi"/>.</summary>
    public PaymentMode Mode { get; set; }

    /// <summary>The acquirer's terminal id (TID), as in the file.</summary>
    public string? TerminalId { get; set; }

    public DateTimeOffset? TransactionAt { get; set; }

    /// <summary>The Indian calendar day of the transaction.</summary>
    public DateOnly? TransactionOn { get; set; }

    /// <summary>The transaction's amount, always positive; <see cref="Type"/> says which way the money went.</summary>
    public decimal Amount { get; set; }

    public decimal Charges { get; set; }
    public decimal Tax { get; set; }

    /// <summary>What the transaction added to the payout (negative for a refund or chargeback).</summary>
    public decimal Net { get; set; }

    public string? Rrn { get; set; }
    public string? ApprovalCode { get; set; }
    public string? CardLast4 { get; set; }

    /// <summary>The day the acquirer paid the bank: the date of the settlement journal.</summary>
    public DateOnly SettledOn { get; set; }

    /// <summary>The bank's reference for the credit, to find it on the bank statement.</summary>
    public string? Utr { get; set; }

    public PosSettlementMatch Match { get; set; }
    public PosMatchBasis? MatchBasis { get; set; }
    public Guid? ReceiptId { get; set; }

    /// <summary>The payment line of the receipt; a payment is matched by one line at most.</summary>
    public Guid? ReceiptPaymentId { get; set; }

    public Guid? RefundId { get; set; }

    /// <summary>
    /// The counter that took the payment or refund it matched when that was not Billing's (<c>pharmacy</c>, <c>canteen</c>),
    /// with the counter's own id of it (matched by one line at most) and its invoice or bill number.
    /// </summary>
    public string? Counter { get; set; }

    public Guid? CounterPaymentId { get; set; }
    public string? CounterDocument { get; set; }

    /// <summary>What HC360 recorded for the matched payment or refund.</summary>
    public decimal? RecordedAmount { get; set; }

    public DateTimeOffset? MatchedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
    public string? ReviewedByName { get; set; }
    public string? ReviewNote { get; set; }

    /// <summary>
    /// The day its report was withdrawn as imported in error: its journal is reversed in that day's books, it matches
    /// nothing, and a correct report may bring the transaction again. Null while it stands.
    /// </summary>
    public DateOnly? WithdrawnOn { get; set; }

    public uint Version { get; set; }

    /// <summary>The transaction before charges: positive for money in, negative for money returned or taken back.</summary>
    public decimal Gross => Type == PosSettlementLineType.Sale ? Amount : -Amount;

    /// <summary>Still to be looked at: it does not match, or it is a chargeback, and nobody has reviewed it.</summary>
    public bool NeedsAttention => ReviewedAt is null && NeedsAttentionWhenUnreviewed(Match);

    public static bool NeedsAttentionWhenUnreviewed(PosSettlementMatch match) =>
        match is PosSettlementMatch.AmountDiffers or PosSettlementMatch.NoReceipt or PosSettlementMatch.NoRefund or PosSettlementMatch.Review;

    /// <summary>Whether matching may still change the line: one that matched, or belongs to another counter, stays as it is.</summary>
    public bool IsOpen => Match is not (PosSettlementMatch.Matched or PosSettlementMatch.OtherCounter or PosSettlementMatch.Withdrawn);

    public static PosSettlementLine From(Guid facilityId, Guid importId, string acquirer, PosSettlementRow row) => new()
    {
        FacilityId = facilityId,
        ImportId = importId,
        LineKey = row.Key,
        Acquirer = acquirer,
        Type = row.Type,
        Mode = row.Mode,
        TerminalId = row.TerminalId,
        TransactionAt = row.TransactionAt,
        TransactionOn = row.TransactionOn,
        Amount = row.Amount,
        Charges = row.Charges,
        Tax = row.Tax,
        Net = row.Net,
        Rrn = row.Rrn,
        ApprovalCode = row.ApprovalCode,
        CardLast4 = row.CardLast4,
        SettledOn = row.SettledOn,
        Utr = row.Utr,
        Match = row.Type switch
        {
            PosSettlementLineType.Sale => PosSettlementMatch.NoReceipt,
            PosSettlementLineType.Refund => PosSettlementMatch.NoRefund,
            _ => PosSettlementMatch.Review,
        },
    };

    public void MatchReceipt(Guid receiptId, Guid paymentId, decimal recorded, PosMatchBasis basis, DateTimeOffset now)
    {
        ReceiptId = receiptId;
        ReceiptPaymentId = paymentId;
        RefundId = null;
        RecordedAmount = recorded;
        MatchBasis = basis;
        Match = recorded == Amount ? PosSettlementMatch.Matched : PosSettlementMatch.AmountDiffers;
        MatchedAt = now;
    }

    public void MatchRefund(Guid refundId, decimal recorded, PosMatchBasis basis, DateTimeOffset now)
    {
        ReceiptId = null;
        ReceiptPaymentId = null;
        RefundId = refundId;
        RecordedAmount = recorded;
        MatchBasis = basis;
        Match = recorded == Amount ? PosSettlementMatch.Matched : PosSettlementMatch.AmountDiffers;
        MatchedAt = now;
    }

    /// <summary>A payment (for a refund line, a refund) the pharmacy or the canteen took by card or UPI.</summary>
    public void MatchCounter(string counter, Guid paymentId, string document, decimal recorded, PosMatchBasis basis, DateTimeOffset now)
    {
        Unmatch();
        Counter = counter;
        CounterPaymentId = paymentId;
        CounterDocument = document;
        RecordedAmount = recorded;
        MatchBasis = basis;
        Match = recorded == Amount ? PosSettlementMatch.Matched : PosSettlementMatch.AmountDiffers;
        MatchedAt = now;
    }

    public void MarkOtherCounter()
    {
        Unmatch();
        Match = PosSettlementMatch.OtherCounter;
    }

    /// <summary>Its report withdrawn: the receipt or refund it matched is free again, and it matches nothing more.</summary>
    public void Withdraw(DateOnly on)
    {
        Unmatch();
        Match = PosSettlementMatch.Withdrawn;
        WithdrawnOn = on;
    }

    /// <summary>Back to what nothing matched: no receipt, no refund, or (a chargeback) to review.</summary>
    public void Unmatch()
    {
        ReceiptId = null;
        ReceiptPaymentId = null;
        RefundId = null;
        Counter = null;
        CounterPaymentId = null;
        CounterDocument = null;
        RecordedAmount = null;
        MatchBasis = null;
        MatchedAt = null;
        Match = Type switch
        {
            PosSettlementLineType.Sale => PosSettlementMatch.NoReceipt,
            PosSettlementLineType.Refund => PosSettlementMatch.NoRefund,
            _ => PosSettlementMatch.Review,
        };
    }

    public Result Review(Guid userId, string? userName, string note, DateTimeOffset now)
    {
        if (!NeedsAttentionWhenUnreviewed(Match))
        {
            return Error.Rule("billing.pos_settlement_matched", "This line matches HC360's records; there is nothing to review.");
        }

        if (ReviewedAt is not null)
        {
            return Error.Rule("billing.pos_settlement_reviewed", "This line was already reviewed.");
        }

        ReviewedAt = now;
        ReviewedBy = userId;
        ReviewedByName = userName;
        ReviewNote = note.Trim();
        return Result.Success();
    }
}

/// <summary>One facility's card and UPI payout of a day under one UTR, as the Tally day book journals it.</summary>
/// <param name="Clears">Per mode, the gross the payout clears from that mode's settlement ledger.</param>
public sealed record TallyPosSettlement(
    string Number, DateOnly Date, string? Utr, decimal Net, decimal Charges, decimal Tax, IReadOnlyList<TallyPosClearing> Clears)
{
    public decimal Gross => Clears.Sum(c => c.Gross);

    /// <summary>
    /// The narration of a reversal (the transactions of a report withdrawn as imported in error), whose amounts are
    /// negative, so its entries are the payout journal's turned round. Null for a payout's own journal.
    /// </summary>
    public string? Correction { get; init; }
}

public sealed record TallyPosClearing(PaymentMode Mode, decimal Gross);

/// <summary>The settlement journals of a facility's card and UPI payouts (INT-POS).</summary>
public static class PosSettlementJournal
{
    /// <summary>
    /// The day's lines as journals, one per UTR (lines without a UTR together), so each matches one credit on the bank
    /// statement. A journal's number stays the same however often the day is posted, so Tally alters it.
    /// </summary>
    public static IReadOnlyList<TallyPosSettlement> Journals(IEnumerable<PosSettlementLine> lines, DateOnly date, string? facilityCode) =>
        lines.Where(l => l.SettledOn == date)
            .GroupBy(l => l.Utr ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TallyPosSettlement(
                VoucherNumber(facilityCode, date, g.Key.Length == 0 ? null : g.Key),
                date,
                g.Key.Length == 0 ? null : g.First().Utr,
                g.Sum(l => l.Net),
                g.Sum(l => l.Charges),
                g.Sum(l => l.Tax),
                g.GroupBy(l => l.Mode).OrderBy(m => m.Key).Select(m => new TallyPosClearing(m.Key, m.Sum(l => l.Gross))).ToList()))
            .Where(j => j.Net != 0 || j.Charges != 0 || j.Tax != 0 || j.Gross != 0)
            .ToList();

    /// <summary>
    /// The reversals in the books of <paramref name="date"/>: the transactions of each report withdrawn that day, per
    /// payout (its day and UTR), turned round. A payout's own journal stays in its day, which may be in Tally already;
    /// the reversal is numbered after it and the report (<c>POS-NH-&lt;UTR&gt;-W1A2B3C</c>).
    /// </summary>
    public static IReadOnlyList<TallyPosSettlement> Withdrawals(IEnumerable<PosSettlementLine> lines, DateOnly date, string? facilityCode) =>
        lines.Where(l => l.WithdrawnOn == date)
            .GroupBy(l => (l.ImportId, l.SettledOn, Utr: (l.Utr ?? string.Empty).ToUpperInvariant()))
            .OrderBy(g => g.Key.SettledOn).ThenBy(g => g.Key.Utr, StringComparer.Ordinal).ThenBy(g => g.Key.ImportId)
            .Select(g =>
            {
                var journal = VoucherNumber(facilityCode, g.Key.SettledOn, g.Key.Utr.Length == 0 ? null : g.Key.Utr);
                var settledOn = g.Key.SettledOn.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
                return new TallyPosSettlement(
                    $"{journal}-W{g.Key.ImportId.ToString("N")[..6].ToUpperInvariant()}",
                    date,
                    g.Key.Utr.Length == 0 ? null : g.First().Utr,
                    -g.Sum(l => l.Net),
                    -g.Sum(l => l.Charges),
                    -g.Sum(l => l.Tax),
                    g.GroupBy(l => l.Mode).OrderBy(m => m.Key).Select(m => new TallyPosClearing(m.Key, -m.Sum(l => l.Gross))).ToList())
                {
                    Correction = $"Reverses card and UPI settlement journal {journal} of {settledOn}: its report was withdrawn as imported in error",
                };
            })
            .Where(j => j.Net != 0 || j.Charges != 0 || j.Tax != 0 || j.Gross != 0)
            .ToList();

    /// <summary>
    /// <c>POS-NH-&lt;UTR&gt;</c>, or <c>POS-NH-20261007</c> for the day's lines without a UTR. The facility's code keeps
    /// two facilities apart when they share a Tally company; only letters and digits of the UTR are used.
    /// </summary>
    public static string VoucherNumber(string? facilityCode, DateOnly date, string? utr)
    {
        var facility = TallyCounters.CodeOf(facilityCode);
        var key = utr is null ? null : new string(utr.Where(char.IsAsciiLetterOrDigit).Take(30).ToArray()).ToUpperInvariant();
        var tail = string.IsNullOrEmpty(key) ? date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : key;
        return facility is null ? $"POS-{tail}" : $"POS-{facility}-{tail}";
    }
}
