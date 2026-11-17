using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum TallyExportStatus
{
    Queued = 0,

    /// <summary>Tally confirmed every voucher of the day book.</summary>
    Posted,

    /// <summary>Tally is not set up for the facility; the day book is imported into Tally from the file.</summary>
    NotConfigured,

    Failed,

    /// <summary>An accountant imported the downloaded file into Tally and marked the day imported.</summary>
    Imported,
}

/// <summary>Outcome of a reconciliation check of a day.</summary>
public enum TallyCheckStatus
{
    NotChecked = 0,
    Matched,
    Differences,

    /// <summary>The check could not run (Tally could not be reached, for example).</summary>
    Failed,
}

/// <summary>
/// One facility's day book for one date (INT-TALLY): the posting log used for retries, the latest post run, the file
/// downloaded for a manual import and the results of the two reconciliation checks. Every voucher sent is tracked in
/// <see cref="TallyVoucherRecord"/>.
/// </summary>
[NotAudited]
public sealed class TallyExport : Entity, IVersioned
{
    public Guid FacilityId { get; set; }
    public DateOnly Date { get; set; }
    public TallyExportStatus Status { get; set; }
    public int VoucherCount { get; set; }
    public int Attempts { get; set; }
    public int CreatedCount { get; set; }
    public int AlteredCount { get; set; }
    public int ErrorCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public string? Message { get; set; }

    /// <summary>The post run in progress or last finished; outbox messages of an older run are ignored.</summary>
    public Guid? RunId { get; set; }

    /// <summary>When the current run was queued. A run queued before the day ended is posted again the next night.</summary>
    public DateTimeOffset? QueuedAt { get; set; }

    /// <summary>Vouchers of the current day book that Tally confirmed (or that were imported from the file).</summary>
    public int ConfirmedCount { get; set; }

    /// <summary>Vouchers of the current day book that Tally refused.</summary>
    public int RejectedCount { get; set; }

    public DateTimeOffset? FileDownloadedAt { get; set; }

    /// <summary>Fingerprint of the day book in the last file downloaded; marking the day imported requires it to be current.</summary>
    public string? FileFingerprint { get; set; }

    public int FileVoucherCount { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    public Guid? ImportedByUserId { get; set; }
    public string? ImportedByName { get; set; }

    /// <summary>The day book compared with the bills, receipts, day-end and other records it is built from.</summary>
    public TallyCheckStatus RecordsCheck { get; set; }

    public DateTimeOffset? RecordsCheckedAt { get; set; }
    public int RecordsDifferences { get; set; }

    /// <summary>The vouchers HC360 sent compared with what Tally holds (read back over Tally's XML port).</summary>
    public TallyCheckStatus TallyCheck { get; set; }

    public DateTimeOffset? TallyCheckedAt { get; set; }
    public int TallyDifferences { get; set; }
    public string? TallyCheckMessage { get; set; }

    /// <summary>
    /// The day existed before HC360 tracked each voucher (set by the TallyReconciliation migration). Tally may hold its
    /// doctor-fee journals under the old numbers (DF-yyyyMMdd-001), so the day keeps them, and a bill cancelled on it
    /// may have turned the bill's sale in Tally into a credit note, so posting it posts the bill's own day again.
    /// </summary>
    public bool LegacyReferences { get; set; }

    /// <summary>Optimistic concurrency (<c>xmin</c>): a post, a check and "mark imported" never overwrite each other.
    /// </summary>
    public uint Version { get; set; }

    /// <summary>Starts a new post run: the next attempt sends every voucher again, and older messages are ignored.</summary>
    public void StartRun(Guid runId, DateTimeOffset now)
    {
        RunId = runId;
        QueuedAt = now;
        Status = TallyExportStatus.Queued;
        Message = null;
        Attempts = 0;
    }
}

/// <summary>
/// Ledger names in the hospital's Tally company (setting <c>billing.tally.ledgers</c>). Anything not configured falls
/// back to these defaults so a fresh installation still produces a balanced, importable day book.
/// </summary>
public sealed record TallyLedgerMap
{
    public string DefaultIncome { get; init; } = "Hospital Service Income";

    /// <summary>Department code → income ledger (for example LAB → "Laboratory Income").</summary>
    public Dictionary<string, string> IncomeByDepartment { get; init; } = new();

    public string Discount { get; init; } = "Discount Allowed";
    public string OutputCgst { get; init; } = "Output CGST";
    public string OutputSgst { get; init; } = "Output SGST";

    /// <summary>GST on supplies to another state (a payer or buyer registered there).</summary>
    public string OutputIgst { get; init; } = "Output IGST";
    public string RoundOff { get; init; } = "Round Off";
    public string PatientDebtors { get; init; } = "Patient Receivables";
    public string PatientAdvances { get; init; } = "Patient Advances";
    public string PayerReceivables { get; init; } = "Insurance & Corporate Receivables";

    /// <summary>Post payer shares to a ledger named after each payer (sundry debtor), else to <see cref="PayerReceivables"/>.</summary>
    public bool LedgerPerPayer { get; init; } = true;

    /// <summary>Payment mode name → cash/bank ledger (for example Card → "HDFC Card Settlement").</summary>
    public Dictionary<string, string> ModeLedgers { get; init; } = new();

    /// <summary>The bank account the payment gateway settles into (debited with each settlement's payout).</summary>
    public string GatewaySettlementBank { get; init; } = "Bank";

    /// <summary>The gateway's charges on payments, without GST.</summary>
    public string GatewayCharges { get; init; } = "Payment Gateway Charges";

    /// <summary>GST the gateway charged on its fees, which the hospital can claim as input tax credit.</summary>
    public string GatewayInputGst { get; init; } = "Input GST";

    /// <summary>The bank account the card and UPI acquirer settles into (debited with each payout); GST on its charges goes to <see cref="GatewayInputGst"/>.</summary>
    public string PosSettlementBank { get; init; } = "Bank";

    /// <summary>The acquirer's charges on card and UPI payments (MDR), without GST.</summary>
    public string PosCharges { get; init; } = "Card & UPI Charges (MDR)";

    public string CreditNotes { get; init; } = "Sales Returns & Allowances";
    public string WriteOffs { get; init; } = "Bad Debts Written Off";
    public string InsuranceDisallowances { get; init; } = "Insurance Disallowances";
    public string DoctorFeeExpense { get; init; } = "Doctor Professional Fees";
    public string DoctorPayablePrefix { get; init; } = "Doctor Fees Payable - ";
    public string TdsPayable { get; init; } = "TDS Payable (194J)";
    public string DoctorRecoveries { get; init; } = "Doctor Recoveries";
    public string PayoutBank { get; init; } = "Bank";

    /// <summary>Goods handed back to suppliers on debit notes, at cost without GST.</summary>
    public string PurchaseReturns { get; init; } = "Purchase Returns";

    /// <summary>Input GST a debit note takes back (the credit claimed when the goods were bought).</summary>
    public string InputCgst { get; init; } = "Input CGST";
    public string InputSgst { get; init; } = "Input SGST";
    public string InputIgst { get; init; } = "Input IGST";

    /// <summary>What a supplier payable adjustment is set against: a rate difference, a scheme credit, freight billed later.</summary>
    public string VendorAdjustments { get; init; } = "Purchase Rate Differences";

    /// <summary>Post to a ledger named after each supplier (sundry creditor), else to <see cref="SundryCreditors"/>.</summary>
    public bool LedgerPerVendor { get; init; } = true;
    public string SundryCreditors { get; init; } = "Sundry Creditors";

    /// <summary>Pharmacy counter sales and returns (their own income ledger; GST on the output GST ledgers).</summary>
    public TallyCounterLedgers Pharmacy { get; init; } = new();

    /// <summary>Canteen sales; staff credit goes to <see cref="TallyCounterLedgers.OnAccount"/>.</summary>
    public TallyCounterLedgers Canteen { get; init; } = new();

    /// <summary>Pharmacy and canteen sales go to one income ledger per GST rate ("Pharmacy Sales @ 12%"), which Tally's GST reports need.</summary>
    public bool SalesLedgerPerGstRate { get; init; } = true;

    /// <summary>The voucher types the hospital's sales are entered under in Tally: one each for IP, OP, laboratory and pharmacy sales.</summary>
    public TallyVoucherTypes VoucherTypes { get; init; } = new();

    /// <summary>
    /// What the narration of a patient's voucher says of the patient, before the document numbers: a bill, its
    /// cancellation, the patient's receipts, refunds, credit notes and write-offs, and the pharmacy's invoices, returns and
    /// balances collected. The name and UHID by default (the hospital's decision of 17 November 2026); a patient whose
    /// name is withheld (<see cref="TallyPatient.Restricted"/>) is named by UHID only, whatever this says.
    /// </summary>
    public TallyNarrationPatient PatientInNarration { get; init; } = TallyNarrationPatient.NameAndUhid;

    /// <summary>
    /// The ledgers HC360 posts to that are bank accounts in Tally: their lines carry the cheque or UTR and its date (Tally's
    /// bank allocations, T15). Null: the ledgers HC360 posts bank money to (<see cref="DefaultBankLedgers"/>), as they are
    /// named now. A cheque ledger gets bank details only when it is listed here.
    /// </summary>
    public List<string>? BankLedgers { get; init; }

    /// <summary>
    /// The version of the facility's saved names, shown with them and sent back with a change (REL-04); none while the
    /// facility uses the hospital-wide names. Never saved with the names.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public uint? Version { get; init; }

    /// <summary>The ledgers of a counter that bills its own sales, with the built-in names for anything not set.</summary>
    public TallyCounterLedgers CounterLedgers(string counter) => TallyCounters.Ledgers(this, counter);

    public string IncomeFor(string? departmentCode) =>
        departmentCode is not null && Find(IncomeByDepartment, departmentCode) is { } ledger ? ledger : DefaultIncome;

    public string ModeLedger(PaymentMode mode) => Find(ModeLedgers, mode.ToString()) ?? mode switch
    {
        PaymentMode.Cash => "Cash",
        PaymentMode.Card => "Card Settlement Account",
        PaymentMode.Upi => "UPI Settlement Account",
        PaymentMode.Cheque => "Cheques in Hand",
        PaymentMode.PaymentGateway => "Payment Gateway Clearing",
        PaymentMode.Wallet => "Wallet Clearing",
        PaymentMode.Advance => PatientAdvances,
        _ => "Bank",
    };

    public string PayerLedger(string? payerName) => LedgerPerPayer && !string.IsNullOrWhiteSpace(payerName) ? payerName.Trim() : PayerReceivables;

    /// <summary>
    /// The ledgers HC360 posts bank money to: the net banking and insurance mode ledgers, the doctors' payout bank, and
    /// the banks the payment gateway and the card acquirer settle into.
    /// </summary>
    public IReadOnlyList<string> DefaultBankLedgers() =>
        new[] { ModeLedger(PaymentMode.NetBanking), ModeLedger(PaymentMode.Insurance), PayoutBank, GatewaySettlementBank, PosSettlementBank }
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Whether <paramref name="ledger"/> is a bank account in Tally (<see cref="BankLedgers"/>), ignoring case as Tally does.</summary>
    public bool IsBankLedger(string ledger) =>
        (BankLedgers ?? DefaultBankLedgers()).Any(l => string.Equals(l?.Trim(), ledger.Trim(), StringComparison.OrdinalIgnoreCase));

    public string DoctorPayable(string doctorName) => DoctorPayablePrefix + doctorName;

    public string VendorLedger(string? vendorName) => LedgerPerVendor && !string.IsNullOrWhiteSpace(vendorName) ? vendorName.Trim() : SundryCreditors;

    private static string? Find(Dictionary<string, string>? map, string key) =>
        map?.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value)).Value;
}

/// <summary>
/// The names of the voucher types (under Sales in Tally) that the day book's sales go to (setting <c>billing.tally.ledgers</c>,
/// under <c>voucherTypes</c>): the hospital keeps IP, OP, laboratory and pharmacy sales apart. The accountant creates
/// them in Tally before the first post; Tally refuses a voucher whose type it does not have.
/// </summary>
public sealed record TallyVoucherTypes
{
    /// <summary>In-patient bills (<see cref="BillType.InPatient"/>), interim and final.</summary>
    public string InPatient { get; init; } = "IP Sales";

    /// <summary>Out-patient bills that are not laboratory bills.</summary>
    public string OutPatient { get; init; } = "OP Sales";

    /// <summary>Out-patient bills whose every service is a laboratory test.</summary>
    public string Laboratory { get; init; } = "Lab Sales";

    /// <summary>The pharmacy counter's invoices, each a voucher of its own.</summary>
    public string Pharmacy { get; init; } = "Pharmacy Sales";
}

/// <summary>How much of the patient a voucher's narration names (<see cref="TallyLedgerMap.PatientInNarration"/>).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<TallyNarrationPatient>))]
public enum TallyNarrationPatient
{
    /// <summary>Document numbers only.</summary>
    None = 0,

    /// <summary>The patient's UHID, never the name.</summary>
    UhidOnly,

    /// <summary>The patient's name and UHID; the UHID alone for a patient whose name is withheld.</summary>
    NameAndUhid,
}

/// <summary>
/// The patient a voucher belongs to, as its narration may name them: the UHID and name from the registration, or, for a
/// walk-in pharmacy customer without a UHID, the name given at the counter.
/// </summary>
/// <param name="Restricted">
/// The name is never written: a restricted (VIP) record, or one in a protected category (HIV, mental health, MTP, sexual
/// violence). <see cref="Registered"/> applies the rule HC360 withholds names by elsewhere (search, reports, the portal).
/// </param>
public sealed record TallyPatient(string? Uhid, string? Name, bool Restricted = false)
{
    /// <summary>A registered patient, restricted when the record is a VIP's or in a protected category.</summary>
    public static TallyPatient Registered(string uhid, string fullName, bool isVip, SensitiveCategory categories) =>
        new(uhid, fullName, isVip || categories != SensitiveCategory.None);

    /// <summary>
    /// What the narration says of the patient, or null when it says nothing: "Ravi Kumar (UHID NHU26000123)", "UHID
    /// NHU26000123", or a walk-in customer's name.
    /// </summary>
    public string? Describe(TallyNarrationPatient shown)
    {
        var uhid = OneLine(Uhid);
        var name = Restricted ? null : OneLine(Name);
        return shown switch
        {
            TallyNarrationPatient.UhidOnly => uhid is null ? null : $"UHID {uhid}",
            TallyNarrationPatient.NameAndUhid when uhid is null => name,
            TallyNarrationPatient.NameAndUhid => name is null ? $"UHID {uhid}" : $"{name} (UHID {uhid})",
            _ => null,
        };
    }

    /// <summary>The text on one line, its spaces collapsed; null when nothing is left.</summary>
    private static string? OneLine(string? value)
    {
        var text = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return text.Length == 0 ? null : text;
    }
}

/// <summary>Which of the hospital's sales voucher types a bill goes to.</summary>
public enum TallySaleKind
{
    OutPatient = 0,
    InPatient,

    /// <summary>An out-patient bill of laboratory tests only.</summary>
    Laboratory,
}

/// <summary>Income of one bill line: <paramref name="Income"/> excludes the package-covered part; discounts include tariff concessions.</summary>
public sealed record TallySaleLine(string? DepartmentCode, string? DepartmentName, decimal Income, decimal Discount, decimal Cgst, decimal Sgst, decimal Igst = 0);

public sealed record TallySale(
    string BillNumber, DateOnly Date, string? PayerName, decimal PatientAmount, decimal PayerAmount, decimal RoundOff, IReadOnlyList<TallySaleLine> Lines, bool IsCancellation)
{
    /// <summary>The voucher type the bill's sale goes to (<see cref="TallyVoucherBuilder.KindOf"/>); its cancellation is a Credit Note whatever it is.</summary>
    public TallySaleKind Kind { get; init; }

    /// <summary>The bill's patient, for the narrations of its sale and its cancellation.</summary>
    public TallyPatient? Patient { get; init; }
}

/// <summary>A payment of a receipt in one mode, with what identifies it at the bank.</summary>
public sealed record TallyPayment(PaymentMode Mode, decimal Amount)
{
    /// <summary>The UTR, transaction id or RRN.</summary>
    public string? Reference { get; init; }

    public string? ChequeNumber { get; init; }
    public string? ChequeBank { get; init; }
    public DateOnly? ChequeDate { get; init; }
}

public sealed record TallyReceipt(string Number, DateOnly Date, ReceiptPurpose Purpose, string? PayerName, IReadOnlyList<TallyPayment> Payments)
{
    /// <summary>The patient the receipt was recorded for, for its narration.</summary>
    public TallyPatient? Patient { get; init; }

    /// <summary>The bill an insurer's settlement settles: its ledger line is set against that bill.</summary>
    public string? BillNumber { get; init; }
}

public sealed record TallyDisallowance(string Reference, DateOnly Date, string? PayerName, decimal Amount)
{
    /// <summary>The bill whose claim the payer settled less: the deduction is set against it on the payer's ledger.</summary>
    public string? BillNumber { get; init; }
}

public sealed record TallyRefund(string Number, DateOnly Date, RefundPurpose Purpose, PaymentMode Mode, decimal Amount)
{
    /// <summary>The patient paid back, for the narration.</summary>
    public TallyPatient? Patient { get; init; }

    /// <summary>The cheque number, UTR or transaction reference the money went out under.</summary>
    public string? Reference { get; init; }
}

/// <summary>
/// Money receipt <paramref name="ReceiptNumber"/> recorded that never arrived, reversed on <paramref name="Date"/>
/// (<see cref="ReceiptReversal"/>): the payments taken back, by mode. An advance adjustment undone with the advance's
/// money has only mode Advance. <paramref name="Chargeback"/>: the money did arrive, and the patient's bank took it back.
/// </summary>
public sealed record TallyReceiptReversal(
    string Number, DateOnly Date, string ReceiptNumber, ReceiptPurpose Purpose, string? PayerName, IReadOnlyList<TallyPayment> Payments,
    bool Chargeback = false);

/// <summary>Refund <paramref name="RefundNumber"/>, recorded as paid out by <paramref name="Mode"/>, never reached the patient (<see cref="RefundReversal"/>).</summary>
public sealed record TallyRefundReversal(string Number, DateOnly Date, string RefundNumber, RefundPurpose Purpose, PaymentMode Mode, decimal Amount);

/// <summary>
/// A credit note or write-off of the day. <paramref name="Cgst"/>, <paramref name="Sgst"/> and <paramref name="Igst"/>: the
/// output GST a credit note takes back (CGST Act s.34), from the lines it corrects; none on a write-off.
/// </summary>
public sealed record TallyCreditNote(string Number, DateOnly Date, CreditNoteKind Kind, decimal Amount, decimal Cgst = 0m, decimal Sgst = 0m, decimal Igst = 0m)
{
    /// <summary>The patient of the bill the note corrects or writes off, for the narration.</summary>
    public TallyPatient? Patient { get; init; }
}

/// <param name="PractitionerCode">The doctor's master code; it makes the accrual journal's number stable across re-posts.</param>
/// <param name="PractitionerId">Tells apart two doctors who share a code (or, without codes, a name).</param>
public sealed record TallyDoctorFee(
    string PractitionerName, decimal Amount, string? PractitionerCode = null, Guid? PractitionerId = null);

public sealed record TallyPayout(string Number, DateOnly Date, string PractitionerName, decimal Gross, decimal Tds, decimal Deductions, decimal Net)
{
    /// <summary>How the doctor was paid: a bank transfer, a cheque, UPI or cash.</summary>
    public PaymentMode? Mode { get; init; }

    /// <summary>The transfer's UTR or the cheque's number.</summary>
    public string? Reference { get; init; }
}

/// <summary>
/// One facility's part of a payment-gateway settlement: <paramref name="Net"/> reached the bank, <paramref name="Charges"/>
/// (without GST) and <paramref name="Tax"/> (GST on them) are the gateway's fees.
/// </summary>
public sealed record TallyGatewaySettlement(string Number, DateOnly Date, string SettlementId, string? Utr, decimal Net, decimal Charges, decimal Tax)
{
    /// <summary>What the settlement clears from the payment-gateway clearing ledger.</summary>
    public decimal Gross => Net + Charges + Tax;

    /// <summary>
    /// The narration of a correction of the settlement's journal (its reversal when the payout failed, or its
    /// restoration), whose amounts carry the correction's sign: a reversal's are negative, so its entries are the
    /// journal's turned round. Null for the settlement's own journal.
    /// </summary>
    public string? Correction { get; init; }
}

/// <summary>Everything that happened in the books of one facility on one day.</summary>
public sealed record TallyDayBook(
    DateOnly Date,
    IReadOnlyList<TallySale> Sales,
    IReadOnlyList<TallyReceipt> Receipts,
    IReadOnlyList<TallyDisallowance> Disallowances,
    IReadOnlyList<TallyRefund> Refunds,
    IReadOnlyList<TallyCreditNote> CreditNotes,
    IReadOnlyList<TallyDoctorFee> DoctorFees,
    IReadOnlyList<TallyPayout> Payouts,
    IReadOnlyList<TallyGatewaySettlement>? GatewaySettlements = null)
{
    /// <summary>The facility's code: it keeps the day book's own voucher numbers apart when facilities share a Tally company.</summary>
    public string? FacilityCode { get; init; }

    /// <summary>The day of each counter that bills its own sales (pharmacy, canteen), from their modules.</summary>
    public IReadOnlyList<CounterSalesDay> Counters { get; init; } = [];

    /// <summary>
    /// The day was posted before HC360 tracked each voucher: doctor-fee journals keep their old numbers.
    /// </summary>
    public bool LegacyDoctorFeeNumbers { get; init; }

    /// <summary>The card and UPI acquirer's payouts of the day, from the settlement reports imported (INT-POS).</summary>
    public IReadOnlyList<TallyPosSettlement> PosSettlements { get; init; } = [];

    /// <summary>Money receipts recorded that never arrived, reversed on the day.</summary>
    public IReadOnlyList<TallyReceiptReversal> ReceiptReversals { get; init; } = [];

    /// <summary>Refunds recorded as paid out that never reached the patient, reversed on the day.</summary>
    public IReadOnlyList<TallyRefundReversal> RefundReversals { get; init; } = [];

    /// <summary>Purchase returns (debit notes) and supplier payable adjustments posted on the day, each approved by a second person.</summary>
    public VendorAccountsDay Vendors { get; init; } = VendorAccountsDay.None;

    public bool IsEmpty => Sales.Count + Receipts.Count + Disallowances.Count + Refunds.Count + CreditNotes.Count + DoctorFees.Count + Payouts.Count
                           + (GatewaySettlements?.Count ?? 0) + PosSettlements.Count + ReceiptReversals.Count + RefundReversals.Count
                           + Vendors.DebitNotes.Count + Vendors.Adjustments.Count == 0
                           && Counters.All(c => c.IsEmpty);
}

/// <summary>
/// Builds balanced Tally vouchers from a day book (INT-TALLY): a sales voucher per bill, under the IP, OP or laboratory
/// sales voucher type (income by department ledger, discount, CGST/SGST, round-off, patient and payer receivables),
/// Credit Notes for cancellations, credit notes and write-offs, Receipts by payment mode, Payments for refunds and doctor
/// payouts (with TDS) and Journals for advance adjustments, insurance disallowances, doctor-fee accruals and reversals of
/// money that did not move (each the entries of its receipt or refund turned round, on the day of the reversal).
/// Narrations carry document numbers; a patient's voucher names the patient first (<see cref="Narrate"/>).
/// </summary>
public static class TallyVoucherBuilder
{
    /// <summary>The voucher type of the canteen's (and, summarised, the pharmacy's) day of sales.</summary>
    public const string SalesType = "Sales";
    public const string CreditNoteType = "Credit Note";
    public const string ReceiptType = "Receipt";
    public const string PaymentType = "Payment";
    public const string JournalType = "Journal";
    public const string DebitNoteType = "Debit Note";

    public static IReadOnlyList<AccountingVoucher> Build(TallyDayBook day, TallyLedgerMap map)
    {
        var vouchers = new List<AccountingVoucher?>();
        vouchers.AddRange(day.Sales.Select(s => Sale(s, map)));
        vouchers.AddRange(day.Receipts.Select(r => Receipt(r, map)));
        vouchers.AddRange(day.Disallowances.Select(d => Disallowance(d, map)));
        vouchers.AddRange(day.Refunds.Select(r => Refund(r, map)));
        vouchers.AddRange(day.ReceiptReversals.Select(r => ReceiptReversal(r, map)));
        vouchers.AddRange(day.RefundReversals.Select(r => RefundReversal(r, map)));
        vouchers.AddRange(day.CreditNotes.Select(c => CreditNote(c, map)));
        vouchers.AddRange(DoctorFees(day, map));
        vouchers.AddRange(day.Payouts.Select(p => Payout(p, map)));
        vouchers.AddRange(TallyCounters.Vouchers(day, map));
        vouchers.AddRange((day.GatewaySettlements ?? []).Select(s => SettlementVoucher(s, map)));
        vouchers.AddRange(day.PosSettlements.Select(s => PosSettlementVoucher(s, map)));
        vouchers.AddRange(day.Vendors.DebitNotes.Select(n => VendorDebitNote(n, day.Date, map)));
        vouchers.AddRange(day.Vendors.Adjustments.Select(a => VendorAdjustment(a, day.Date, map)));
        return vouchers.OfType<AccountingVoucher>().ToList();
    }

    /// <summary>
    /// A purchase return: the supplier is owed the whole note less, the goods leave purchases at cost and the input GST
    /// claimed on them is taken back.
    /// </summary>
    private static AccountingVoucher? VendorDebitNote(VendorDebitNoteEntry note, DateOnly date, TallyLedgerMap map)
    {
        var entries = new EntryList();

        // Against the supplier's invoice the goods came on, when the note names its goods receipt.
        entries.Debit(map.VendorLedger(note.VendorName), note.Total, bill: TallyBillRef.Against(note.SupplierInvoiceNumber));
        entries.Credit(map.PurchaseReturns, note.Taxable);
        entries.Credit(map.InputCgst, note.Cgst);
        entries.Credit(map.InputSgst, note.Sgst);
        entries.Credit(map.InputIgst, note.Igst);
        var narration = note.VendorReference is { Length: > 0 } reference ? $"Purchase return {note.Number} (supplier's ref. {reference})" : $"Purchase return {note.Number}";
        return entries.ToVoucher(DebitNoteType, date, note.Number, narration, note.Number);
    }

    /// <summary>A supplier payable adjustment: the supplier's ledger against the adjustments ledger, whichever way it goes.</summary>
    private static AccountingVoucher? VendorAdjustment(VendorPayableAdjustmentEntry adjustment, DateOnly date, TallyLedgerMap map)
    {
        // HC360 does not link an adjustment to an invoice: it stands on the supplier's account.
        var entries = new EntryList();
        if (adjustment.Reduce)
        {
            entries.Debit(map.VendorLedger(adjustment.VendorName), adjustment.Amount, bill: TallyBillRef.OnAccount);
            entries.Credit(map.VendorAdjustments, adjustment.Amount);
        }
        else
        {
            entries.Debit(map.VendorAdjustments, adjustment.Amount);
            entries.Credit(map.VendorLedger(adjustment.VendorName), adjustment.Amount, bill: TallyBillRef.OnAccount);
        }

        return entries.ToVoucher(JournalType, date, adjustment.Number, $"Supplier payable adjustment {adjustment.Number}", adjustment.Number);
    }

    /// <summary>
    /// Number (and reference) of a day's doctor-fee journal: the facility, the date and the doctor's code, so the
    /// same doctor's journal keeps its number however often the day is posted (DF-NH-20260925-DR005).
    /// </summary>
    public static string DoctorFeeNumber(string? facilityCode, DateOnly date, string? practitionerCode, string practitionerName)
    {
        var code = TallyCounters.CodeOf(practitionerCode) ?? TallyCounters.ShortHash(practitionerName);
        var facility = TallyCounters.CodeOf(facilityCode);
        var day = date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        return facility is null ? $"DF-{day}-{code}" : $"DF-{facility}-{day}-{code}";
    }

    /// <summary>
    /// Settlement journal: the payout reaches the bank, the gateway's charges and the GST on them are expenses, and the
    /// gross clears the payment-gateway clearing ledger that the day's online receipts debited (and refunds credited).
    /// </summary>
    private static AccountingVoucher? SettlementVoucher(TallyGatewaySettlement settlement, TallyLedgerMap map)
    {
        var entries = new EntryList();
        entries.Debit(map.GatewaySettlementBank, settlement.Net, bank: Transfer(map, map.GatewaySettlementBank, settlement.Date, settlement.Net, settlement.Utr));
        entries.Debit(map.GatewayCharges, settlement.Charges);
        entries.Debit(map.GatewayInputGst, settlement.Tax);
        entries.Credit(map.ModeLedger(PaymentMode.PaymentGateway), settlement.Gross);
        var narration = settlement.Correction ?? (settlement.Utr is null
            ? $"Payment gateway settlement {settlement.SettlementId}"
            : $"Payment gateway settlement {settlement.SettlementId}, UTR {settlement.Utr}");
        return entries.ToVoucher(JournalType, settlement.Date, settlement.Number, narration, settlement.Number);
    }

    /// <summary>
    /// Card and UPI settlement journal: the payout reaches the bank, the acquirer's charges and the GST on them are
    /// expenses, and the gross (payments less refunds and chargebacks) clears the card and UPI settlement ledgers that
    /// the receipts debited.
    /// </summary>
    private static AccountingVoucher? PosSettlementVoucher(TallyPosSettlement settlement, TallyLedgerMap map)
    {
        var entries = new EntryList();
        entries.Debit(map.PosSettlementBank, settlement.Net, bank: Transfer(map, map.PosSettlementBank, settlement.Date, settlement.Net, settlement.Utr));
        entries.Debit(map.PosCharges, settlement.Charges);
        entries.Debit(map.GatewayInputGst, settlement.Tax);
        foreach (var clearing in settlement.Clears)
        {
            entries.Credit(map.ModeLedger(clearing.Mode), clearing.Gross);
        }

        var narration = settlement.Correction ?? (settlement.Utr is null
            ? $"Card and UPI settlement of {settlement.Date.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture)}"
            : $"Card and UPI settlement, UTR {settlement.Utr}");
        return entries.ToVoucher(JournalType, settlement.Date, settlement.Number, narration, settlement.Number);
    }

    private static AccountingVoucher? Sale(TallySale sale, TallyLedgerMap map)
    {
        var entries = new EntryList();
        entries.Debit(map.PatientDebtors, sale.PatientAmount);

        // The payer's share is a bill of the payer's (New Ref); its cancellation takes it off that bill (Agst Ref).
        entries.Debit(map.PayerLedger(sale.PayerName), sale.PayerAmount, bill: sale.IsCancellation ? TallyBillRef.Against(sale.BillNumber) : TallyBillRef.New(sale.BillNumber));
        entries.Debit(map.Discount, sale.Lines.Sum(l => l.Discount));
        foreach (var department in sale.Lines.GroupBy(l => (l.DepartmentCode, l.DepartmentName)))
        {
            entries.Credit(map.IncomeFor(department.Key.DepartmentCode), department.Sum(l => l.Income), department.Key.DepartmentName);
        }

        entries.Credit(map.OutputCgst, sale.Lines.Sum(l => l.Cgst));
        entries.Credit(map.OutputSgst, sale.Lines.Sum(l => l.Sgst));
        entries.Credit(map.OutputIgst, sale.Lines.Sum(l => l.Igst));
        entries.Credit(map.RoundOff, sale.RoundOff);

        // The reversal needs a reference of its own: with the bill's, Tally would alter the sale into the credit note.
        return sale.IsCancellation
            ? entries.Reversed().ToVoucher(
                CreditNoteType, sale.Date, CancellationNumber(sale.BillNumber), Narrate(sale.Patient, map, $"Cancellation of bill {sale.BillNumber}"), CancellationNumber(sale.BillNumber))
            : entries.ToVoucher(SaleType(sale.Kind, map), sale.Date, sale.BillNumber, Narrate(sale.Patient, map, $"Bill {sale.BillNumber}"), sale.BillNumber);
    }

    /// <summary>
    /// A patient's voucher's narration: what <see cref="TallyLedgerMap.PatientInNarration"/> lets it say of the patient,
    /// then <paramref name="text"/> ("Ravi Kumar (UHID NHU26000123): Bill NHOB260000001").
    /// </summary>
    public static string Narrate(TallyPatient? patient, TallyLedgerMap map, string text) =>
        patient?.Describe(map.PatientInNarration) is { } who ? $"{who}: {text}" : text;

    /// <summary>
    /// The voucher type a bill goes to: an in-patient bill is IP; an out-patient bill whose every line is a laboratory
    /// test is Lab; any other out-patient bill (a bill without lines too) is OP.
    /// </summary>
    public static TallySaleKind KindOf(BillType type, IReadOnlyCollection<ServiceCategory> lineCategories) =>
        type == BillType.InPatient ? TallySaleKind.InPatient
        : lineCategories.Count > 0 && lineCategories.All(c => c == ServiceCategory.Laboratory) ? TallySaleKind.Laboratory
        : TallySaleKind.OutPatient;

    /// <summary>The name of the voucher type a bill of <paramref name="kind"/> goes to in the facility's Tally.</summary>
    public static string SaleType(TallySaleKind kind, TallyLedgerMap map) => kind switch
    {
        TallySaleKind.InPatient => map.VoucherTypes.InPatient,
        TallySaleKind.Laboratory => map.VoucherTypes.Laboratory,
        _ => map.VoucherTypes.OutPatient,
    };

    /// <summary>Number and reference of the credit note that reverses a cancelled bill.</summary>
    public static string CancellationNumber(string billNumber) => billNumber + "/C";

    /// <summary>
    /// Reference of a claim settlement's deduction journal: the claim number and the first six hex digits of the
    /// settlement's id. Those digits come from the id's timestamp, so two settlements of one claim recorded within a few
    /// hours can share them; such settlements add the id's last four (random) digits, so neither alters the other in Tally.
    /// </summary>
    /// <param name="settlementsOfClaim">Every settlement of the claim, this one included.</param>
    public static string ClaimDeductionReference(string claimNumber, Guid settlementId, IEnumerable<Guid> settlementsOfClaim)
    {
        var id = settlementId.ToString("N");
        var shared = settlementsOfClaim.Distinct().Count(s => s.ToString("N")[..6] == id[..6]) > 1;
        return shared ? $"{claimNumber}/{id[..6]}{id[^4..]}" : $"{claimNumber}/{id[..6]}";
    }

    private static AccountingVoucher? Receipt(TallyReceipt receipt, TallyLedgerMap map)
    {
        var entries = ReceiptEntries(receipt.Purpose, receipt.PayerName, receipt.Payments, map, receipt.BillNumber, bankDate: receipt.Date);
        return IsAdjustment(receipt.Payments)
            ? entries.ToVoucher(JournalType, receipt.Date, receipt.Number, Narrate(receipt.Patient, map, $"Advance adjusted, receipt {receipt.Number}"), receipt.Number)
            : entries.ToVoucher(ReceiptType, receipt.Date, receipt.Number, Narrate(receipt.Patient, map, $"Receipt {receipt.Number}"), receipt.Number);
    }

    /// <summary>The receipt's entries for the payments reversed, turned round: the money is owed again, or the advance holds less.</summary>
    private static AccountingVoucher? ReceiptReversal(TallyReceiptReversal reversal, TallyLedgerMap map)
    {
        var entries = ReceiptEntries(reversal.Purpose, reversal.PayerName, reversal.Payments, map).Reversed();
        var narration = IsAdjustment(reversal.Payments)
            ? $"Advance adjustment of receipt {reversal.ReceiptNumber} undone, {reversal.Number}: the advance was not received"
            : reversal.Chargeback
                ? $"Receipt {reversal.ReceiptNumber} reversed, {reversal.Number}: taken back by a chargeback"
                : $"Receipt {reversal.ReceiptNumber} reversed, {reversal.Number}: money not received";
        return entries.ToVoucher(JournalType, reversal.Date, reversal.Number, narration, reversal.Number);
    }

    /// <param name="billNumber">For an insurer's settlement, the bill it settles: the payer's ledger is credited against it.</param>
    /// <param name="bankDate">The receipt's day: the lines on bank ledgers carry each payment's cheque or UTR. None for a reversal.</param>
    private static EntryList ReceiptEntries(
        ReceiptPurpose purpose, string? payerName, IReadOnlyList<TallyPayment> payments, TallyLedgerMap map, string? billNumber = null, DateOnly? bankDate = null)
    {
        var entries = new EntryList();
        foreach (var mode in payments.GroupBy(p => p.Mode))
        {
            var ledger = map.ModeLedger(mode.Key);
            entries.Debit(ledger, mode.Sum(p => p.Amount),
                bank: bankDate is { } day && map.IsBankLedger(ledger) ? mode.Select(p => BankPart(p, day)).ToList() : null);
        }

        var total = payments.Sum(p => p.Amount);
        switch (purpose)
        {
            case ReceiptPurpose.Deposit or ReceiptPurpose.Advance:
                entries.Credit(map.PatientAdvances, total);
                break;
            case ReceiptPurpose.PayerSettlement:
                entries.Credit(map.PayerLedger(payerName), total, bill: TallyBillRef.Against(billNumber));
                break;
            default:
                entries.Credit(map.PatientDebtors, total);
                break;
        }

        return entries;
    }

    private static bool IsAdjustment(IReadOnlyList<TallyPayment> payments) => payments.Count > 0 && payments.All(p => p.Mode == PaymentMode.Advance);

    private static AccountingVoucher? Disallowance(TallyDisallowance disallowance, TallyLedgerMap map)
    {
        var entries = new EntryList();
        entries.Debit(map.InsuranceDisallowances, disallowance.Amount);
        entries.Credit(map.PayerLedger(disallowance.PayerName), disallowance.Amount, bill: TallyBillRef.Against(disallowance.BillNumber));
        return entries.ToVoucher(JournalType, disallowance.Date, disallowance.Reference, $"Claim deductions {disallowance.Reference}", disallowance.Reference);
    }

    private static AccountingVoucher? Refund(TallyRefund refund, TallyLedgerMap map)
    {
        var entries = new EntryList();
        if (refund.Mode == PaymentMode.Advance)
        {
            entries.Debit(map.PatientDebtors, refund.Amount);
            entries.Credit(map.PatientAdvances, refund.Amount);
            return entries.ToVoucher(JournalType, refund.Date, refund.Number, Narrate(refund.Patient, map, $"Advance restored, refund {refund.Number}"), refund.Number);
        }

        return RefundEntries(refund.Purpose, refund.Mode, refund.Amount, map, (refund.Date, refund.Reference))
            .ToVoucher(PaymentType, refund.Date, refund.Number, Narrate(refund.Patient, map, $"Refund {refund.Number}"), refund.Number);
    }

    /// <summary>The refund's payment turned round: the money is back with the hospital, owed to the patient again.</summary>
    private static AccountingVoucher? RefundReversal(TallyRefundReversal reversal, TallyLedgerMap map) =>
        RefundEntries(reversal.Purpose, reversal.Mode, reversal.Amount, map).Reversed()
            .ToVoucher(JournalType, reversal.Date, reversal.Number, $"Refund {reversal.RefundNumber} not paid out, {reversal.Number}", reversal.Number);

    /// <param name="paidOut">The refund's day and reference: a line on a bank ledger carries them. None for a reversal.</param>
    private static EntryList RefundEntries(RefundPurpose purpose, PaymentMode mode, decimal amount, TallyLedgerMap map, (DateOnly Date, string? Reference)? paidOut = null)
    {
        var entries = new EntryList();
        entries.Debit(purpose is RefundPurpose.Deposit or RefundPurpose.Advance ? map.PatientAdvances : map.PatientDebtors, amount);
        var ledger = map.ModeLedger(mode);
        entries.Credit(ledger, amount,
            bank: paidOut is { } paid && map.IsBankLedger(ledger) ? [new AccountingBankAllocation(paid.Date, TransactionOf(mode), amount, Clean(paid.Reference))] : null);
        return entries;
    }

    /// <summary>How a payment in <paramref name="mode"/> moved at the bank: a cheque, a UTR-based transfer (net banking, UPI, an insurer's NEFT), or something else.</summary>
    public static AccountingBankTransaction TransactionOf(PaymentMode mode) => mode switch
    {
        PaymentMode.Cheque => AccountingBankTransaction.Cheque,
        PaymentMode.NetBanking or PaymentMode.Upi or PaymentMode.Insurance => AccountingBankTransaction.EFundTransfer,
        _ => AccountingBankTransaction.Others,
    };

    /// <summary>A received payment's bank details: a cheque's number, date and bank; a transfer's UTR on the receipt's day.</summary>
    private static AccountingBankAllocation BankPart(TallyPayment payment, DateOnly date) =>
        payment.Mode == PaymentMode.Cheque
            ? new(date, AccountingBankTransaction.Cheque, payment.Amount, Clean(payment.ChequeNumber) ?? Clean(payment.Reference), payment.ChequeDate, Clean(payment.ChequeBank))
            : new(date, TransactionOf(payment.Mode), payment.Amount, Clean(payment.Reference));

    /// <summary>
    /// A settlement's credit to the bank, by its UTR (an e-fund transfer, else "Others"), when <paramref name="ledger"/>
    /// is a bank ledger. <paramref name="amount"/> carries the journal's sign (a reversal's is negative).
    /// </summary>
    private static IReadOnlyList<AccountingBankAllocation>? Transfer(TallyLedgerMap map, string ledger, DateOnly date, decimal amount, string? utr) =>
        map.IsBankLedger(ledger)
            ? [new AccountingBankAllocation(date, Clean(utr) is null ? AccountingBankTransaction.Others : AccountingBankTransaction.EFundTransfer, amount, Clean(utr))]
            : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A credit note takes back what it credits: the taxable part from sales returns and the GST from the output GST
    /// ledgers it was credited to on the bill, so the GST in the books falls with the GST the portal is told of. A
    /// write-off reduces no GST.
    /// </summary>
    private static AccountingVoucher? CreditNote(TallyCreditNote note, TallyLedgerMap map)
    {
        var entries = new EntryList();
        var gst = note.Cgst + note.Sgst + note.Igst;
        entries.Debit(note.Kind == CreditNoteKind.WriteOff ? map.WriteOffs : map.CreditNotes, note.Amount - gst);
        entries.Debit(map.OutputCgst, note.Cgst);
        entries.Debit(map.OutputSgst, note.Sgst);
        entries.Debit(map.OutputIgst, note.Igst);
        // A payer credit note turns the settlement's deduction (already debited to insurance disallowances, the payer
        // credited) into a GST credit note: the disallowance comes back out, and sales returns and output GST take it.
        entries.Credit(note.Kind == CreditNoteKind.PayerShare ? map.InsuranceDisallowances : map.PatientDebtors, note.Amount);
        var words = note.Kind switch
        {
            CreditNoteKind.WriteOff => "Write-off",
            CreditNoteKind.PayerShare => "Payer credit note",
            _ => "Credit note",
        };
        return entries.ToVoucher(CreditNoteType, note.Date, note.Number, Narrate(note.Patient, map, $"{words} {note.Number}"), note.Number);
    }

    /// <summary>
    /// Number of a doctor-fee journal of a day posted before HC360 tracked each voucher: the day and a sequence
    /// (DF-20260925-001), as Tally already holds it. Re-posting such a day alters those journals rather than adding
    /// new ones beside them.
    /// </summary>
    public static string LegacyDoctorFeeNumber(DateOnly date, int sequence) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"DF-{date:yyyyMMdd}-{sequence:000}");

    /// <summary>
    /// One journal per doctor. Two doctors whose codes (or, without codes, names) give the same number both add the
    /// last eight hex digits of their id, so neither number depends on the order the doctors come in. A day posted
    /// before per-voucher tracking keeps its old sequence numbers.
    /// </summary>
    private static IEnumerable<AccountingVoucher?> DoctorFees(TallyDayBook day, TallyLedgerMap map)
    {
        var fees = day.DoctorFees
            .OrderBy(f => f.PractitionerCode, StringComparer.Ordinal)
            .ThenBy(f => f.PractitionerName, StringComparer.Ordinal)
            .ThenBy(f => f.PractitionerId)
            .ToList();
        var numbers = fees
            .Select(f => DoctorFeeNumber(day.FacilityCode, day.Date, f.PractitionerCode, f.PractitionerName))
            .ToList();
        var shared = numbers.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fees.Count; i++)
        {
            var fee = fees[i];
            var number = day.LegacyDoctorFeeNumbers ? LegacyDoctorFeeNumber(day.Date, i + 1)
                : shared.Contains(numbers[i]) && fee.PractitionerId is { } id
                    ? numbers[i] + "-" + id.ToString("N")[^8..].ToUpperInvariant()
                : numbers[i];

            var entries = new EntryList();
            entries.Debit(map.DoctorFeeExpense, fee.Amount);
            entries.Credit(map.DoctorPayable(fee.PractitionerName), fee.Amount);
            yield return entries.ToVoucher(JournalType, day.Date, number, $"Doctor fees accrued ({fee.PractitionerName})", number);
        }
    }

    private static AccountingVoucher? Payout(TallyPayout payout, TallyLedgerMap map)
    {
        var entries = new EntryList();
        entries.Debit(map.DoctorPayable(payout.PractitionerName), payout.Gross);
        entries.Credit(map.TdsPayable, payout.Tds);
        entries.Credit(map.DoctorRecoveries, payout.Deductions);
        entries.Credit(map.PayoutBank, payout.Net,
            bank: map.IsBankLedger(map.PayoutBank)
                ? [new AccountingBankAllocation(payout.Date, payout.Mode is { } mode ? TransactionOf(mode) : AccountingBankTransaction.Others, payout.Net, Clean(payout.Reference))]
                : null);
        return entries.ToVoucher(PaymentType, payout.Date, payout.Number, $"Doctor payout {payout.Number}", payout.Number);
    }

    /// <summary>
    /// Collects entries, merging repeats of the same ledger/side and turning negative amounts into the opposite side. A
    /// line on a party's ledger may name the bill it is set against (<see cref="TallyBillRef"/>), and a line on a bank
    /// ledger the cheques and transfers it is made of. Merged lines keep each part; a merged line some of whose parts
    /// named no bill has the rest on account, and one some of whose parts brought no bank details has the rest as
    /// "Others", so a line's allocations always add up to it.
    /// </summary>
    internal sealed class EntryList
    {
        private readonly List<AccountingEntry> _entries = [];

        /// <param name="bank">The line's cheques and transfers, their amounts signed like <paramref name="amount"/>.</param>
        public void Debit(string ledger, decimal amount, string? costCentre = null, TallyBillRef? bill = null, IReadOnlyList<AccountingBankAllocation>? bank = null) =>
            Add(ledger, amount, isDebit: true, costCentre, bill, bank);

        /// <param name="bank">The line's cheques and transfers, their amounts signed like <paramref name="amount"/>.</param>
        public void Credit(string ledger, decimal amount, string? costCentre = null, TallyBillRef? bill = null, IReadOnlyList<AccountingBankAllocation>? bank = null) =>
            Add(ledger, amount, isDebit: false, costCentre, bill, bank);

        public EntryList Reversed()
        {
            var reversed = new EntryList();
            foreach (var entry in _entries)
            {
                reversed.Add(entry with { IsDebit = !entry.IsDebit });
            }

            return reversed;
        }

        public AccountingVoucher? ToVoucher(string type, DateOnly date, string number, string narration, string reference) =>
            _entries.Count == 0 ? null : new AccountingVoucher(type, date, number, narration, _entries.Select(e => Complete(e, date)).ToList(), reference);

        private void Add(string ledger, decimal amount, bool isDebit, string? costCentre, TallyBillRef? bill, IReadOnlyList<AccountingBankAllocation>? bank)
        {
            amount = Money.Round(amount);
            if (amount == 0)
            {
                return;
            }

            var sign = amount < 0 ? -1 : 1;
            if (amount < 0)
            {
                amount = -amount;
                isDebit = !isDebit;
            }

            Add(new AccountingEntry(ledger, amount, isDebit, costCentre)
            {
                BillAllocations = bill is null ? [] : [new AccountingBillAllocation(bill.Name, bill.Type, amount)],
                BankAllocations = (bank ?? [])
                    .Select(b => b with { Amount = Money.Round(sign * b.Amount) })
                    .Where(b => b.Amount != 0)
                    .ToList(),
            });
        }

        private void Add(AccountingEntry entry)
        {
            var index = _entries.FindIndex(e => e.LedgerName == entry.LedgerName && e.IsDebit == entry.IsDebit && e.CostCentre == entry.CostCentre);
            if (index < 0)
            {
                _entries.Add(entry);
                return;
            }

            var held = _entries[index];
            var merged = held with { Amount = held.Amount + entry.Amount };
            if (held.BillAllocations.Count + entry.BillAllocations.Count > 0)
            {
                merged = merged with { BillAllocations = Bills([.. CompleteBills(held).BillAllocations, .. CompleteBills(entry).BillAllocations]) };
            }

            if (held.BankAllocations.Count + entry.BankAllocations.Count > 0)
            {
                // Parts without bank details are completed when the voucher's date is known.
                merged = merged with { BankAllocations = [.. held.BankAllocations, .. entry.BankAllocations, .. Unknown(held), .. Unknown(entry)] };
            }

            _entries[index] = merged;
        }

        /// <summary>A part merged in without bank details, as a placeholder dated when the voucher is made.</summary>
        private static IEnumerable<AccountingBankAllocation> Unknown(AccountingEntry entry) =>
            entry.BankAllocations.Count == 0 ? [new AccountingBankAllocation(DateOnly.MinValue, AccountingBankTransaction.Others, entry.Amount)] : [];

        /// <summary>The line with what its allocations leave out on account, or as "Others" on <paramref name="date"/>.</summary>
        private static AccountingEntry Complete(AccountingEntry entry, DateOnly date)
        {
            entry = CompleteBills(entry);
            if (entry.BankAllocations.Count == 0)
            {
                return entry;
            }

            var parts = entry.BankAllocations.Select(b => b.Date == DateOnly.MinValue ? b with { Date = date } : b).ToList();
            var rest = entry.Amount - parts.Sum(b => b.Amount);
            return entry with { BankAllocations = rest > 0 ? [.. parts, new AccountingBankAllocation(date, AccountingBankTransaction.Others, rest)] : parts };
        }

        private static AccountingEntry CompleteBills(AccountingEntry entry)
        {
            var rest = entry.Amount - entry.BillAllocations.Sum(b => b.Amount);
            return entry.BillAllocations.Count == 0 || rest <= 0
                ? entry
                : entry with { BillAllocations = Bills([.. entry.BillAllocations, new AccountingBillAllocation(null, AccountingBillType.OnAccount, rest)]) };
        }

        /// <summary>One part per bill and kind of reference, in the order they came.</summary>
        private static List<AccountingBillAllocation> Bills(IEnumerable<AccountingBillAllocation> parts) =>
            parts.GroupBy(b => (b.Name, b.Type))
                .Select(g => new AccountingBillAllocation(g.Key.Name, g.Key.Type, g.Sum(b => b.Amount)))
                .ToList();
    }
}

/// <summary>
/// The bill a line on a party's ledger (a payer, a supplier) is set against, so the party's outstanding shows bill by bill
/// in Tally (T14): a new bill (a sale's payer share, a purchase), against a bill (a settlement, a deduction, a return), or
/// on account when the bill is not known.
/// </summary>
public sealed record TallyBillRef(string? Name, AccountingBillType Type)
{
    public static TallyBillRef OnAccount { get; } = new(null, AccountingBillType.OnAccount);

    public static TallyBillRef New(string name) => new(name.Trim(), AccountingBillType.NewRef);

    /// <summary>Against the bill <paramref name="name"/>, or on account when it is not known.</summary>
    public static TallyBillRef Against(string? name) => string.IsNullOrWhiteSpace(name) ? OnAccount : new(name.Trim(), AccountingBillType.AgainstRef);
}
