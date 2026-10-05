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

    public string CreditNotes { get; init; } = "Sales Returns & Allowances";
    public string WriteOffs { get; init; } = "Bad Debts Written Off";
    public string InsuranceDisallowances { get; init; } = "Insurance Disallowances";
    public string DoctorFeeExpense { get; init; } = "Doctor Professional Fees";
    public string DoctorPayablePrefix { get; init; } = "Doctor Fees Payable - ";
    public string TdsPayable { get; init; } = "TDS Payable (194J)";
    public string DoctorRecoveries { get; init; } = "Doctor Recoveries";
    public string PayoutBank { get; init; } = "Bank";

    /// <summary>Pharmacy counter sales and returns (their own income ledger; GST on the output GST ledgers).</summary>
    public TallyCounterLedgers Pharmacy { get; init; } = new();

    /// <summary>Canteen sales; staff credit goes to <see cref="TallyCounterLedgers.OnAccount"/>.</summary>
    public TallyCounterLedgers Canteen { get; init; } = new();

    /// <summary>Pharmacy and canteen sales go to one income ledger per GST rate ("Pharmacy Sales @ 12%"), which Tally's GST reports need.</summary>
    public bool SalesLedgerPerGstRate { get; init; } = true;

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

    public string DoctorPayable(string doctorName) => DoctorPayablePrefix + doctorName;

    private static string? Find(Dictionary<string, string>? map, string key) =>
        map?.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value)).Value;
}

/// <summary>Income of one bill line: <paramref name="Income"/> excludes the package-covered part; discounts include tariff concessions.</summary>
public sealed record TallySaleLine(string? DepartmentCode, string? DepartmentName, decimal Income, decimal Discount, decimal Cgst, decimal Sgst);

public sealed record TallySale(
    string BillNumber, DateOnly Date, string? PayerName, decimal PatientAmount, decimal PayerAmount, decimal RoundOff, IReadOnlyList<TallySaleLine> Lines, bool IsCancellation);

public sealed record TallyPayment(PaymentMode Mode, decimal Amount);

public sealed record TallyReceipt(string Number, DateOnly Date, ReceiptPurpose Purpose, string? PayerName, IReadOnlyList<TallyPayment> Payments);

public sealed record TallyDisallowance(string Reference, DateOnly Date, string? PayerName, decimal Amount);

public sealed record TallyRefund(string Number, DateOnly Date, RefundPurpose Purpose, PaymentMode Mode, decimal Amount);

public sealed record TallyCreditNote(string Number, DateOnly Date, CreditNoteKind Kind, decimal Amount);

/// <param name="PractitionerCode">The doctor's master code; it makes the accrual journal's number stable across re-posts.</param>
/// <param name="PractitionerId">Tells apart two doctors who share a code (or, without codes, a name).</param>
public sealed record TallyDoctorFee(
    string PractitionerName, decimal Amount, string? PractitionerCode = null, Guid? PractitionerId = null);

public sealed record TallyPayout(string Number, DateOnly Date, string PractitionerName, decimal Gross, decimal Tds, decimal Deductions, decimal Net);

/// <summary>
/// One facility's part of a payment-gateway settlement: <paramref name="Net"/> reached the bank, <paramref name="Charges"/>
/// (without GST) and <paramref name="Tax"/> (GST on them) are the gateway's fees.
/// </summary>
public sealed record TallyGatewaySettlement(string Number, DateOnly Date, string SettlementId, string? Utr, decimal Net, decimal Charges, decimal Tax)
{
    /// <summary>What the settlement clears from the payment-gateway clearing ledger.</summary>
    public decimal Gross => Net + Charges + Tax;
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

    public bool IsEmpty => Sales.Count + Receipts.Count + Disallowances.Count + Refunds.Count + CreditNotes.Count + DoctorFees.Count + Payouts.Count
                           + (GatewaySettlements?.Count ?? 0) == 0
                           && Counters.All(c => c.IsEmpty);
}

/// <summary>
/// Builds balanced Tally vouchers from a day book (INT-TALLY): a Sales voucher per bill (income by department ledger,
/// discount, CGST/SGST, round-off, patient and payer receivables), Credit Notes for cancellations, credit notes and
/// write-offs, Receipts by payment mode, Payments for refunds and doctor payouts (with TDS) and Journals for advance
/// adjustments, insurance disallowances and doctor-fee accruals. Narrations carry document numbers, never patient names.
/// </summary>
public static class TallyVoucherBuilder
{
    public const string SalesType = "Sales";
    public const string CreditNoteType = "Credit Note";
    public const string ReceiptType = "Receipt";
    public const string PaymentType = "Payment";
    public const string JournalType = "Journal";

    public static IReadOnlyList<AccountingVoucher> Build(TallyDayBook day, TallyLedgerMap map)
    {
        var vouchers = new List<AccountingVoucher?>();
        vouchers.AddRange(day.Sales.Select(s => Sale(s, map)));
        vouchers.AddRange(day.Receipts.Select(r => Receipt(r, map)));
        vouchers.AddRange(day.Disallowances.Select(d => Disallowance(d, map)));
        vouchers.AddRange(day.Refunds.Select(r => Refund(r, map)));
        vouchers.AddRange(day.CreditNotes.Select(c => CreditNote(c, map)));
        vouchers.AddRange(DoctorFees(day, map));
        vouchers.AddRange(day.Payouts.Select(p => Payout(p, map)));
        vouchers.AddRange(TallyCounters.Vouchers(day, map));
        vouchers.AddRange((day.GatewaySettlements ?? []).Select(s => SettlementVoucher(s, map)));
        return vouchers.OfType<AccountingVoucher>().ToList();
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
        entries.Debit(map.GatewaySettlementBank, settlement.Net);
        entries.Debit(map.GatewayCharges, settlement.Charges);
        entries.Debit(map.GatewayInputGst, settlement.Tax);
        entries.Credit(map.ModeLedger(PaymentMode.PaymentGateway), settlement.Gross);
        var narration = settlement.Utr is null
            ? $"Payment gateway settlement {settlement.SettlementId}"
            : $"Payment gateway settlement {settlement.SettlementId}, UTR {settlement.Utr}";
        return entries.ToVoucher(JournalType, settlement.Date, settlement.Number, narration, settlement.Number);
    }

    private static AccountingVoucher? Sale(TallySale sale, TallyLedgerMap map)
    {
        var entries = new EntryList();
        entries.Debit(map.PatientDebtors, sale.PatientAmount);
        entries.Debit(map.PayerLedger(sale.PayerName), sale.PayerAmount);
        entries.Debit(map.Discount, sale.Lines.Sum(l => l.Discount));
        foreach (var department in sale.Lines.GroupBy(l => (l.DepartmentCode, l.DepartmentName)))
        {
            entries.Credit(map.IncomeFor(department.Key.DepartmentCode), department.Sum(l => l.Income), department.Key.DepartmentName);
        }

        entries.Credit(map.OutputCgst, sale.Lines.Sum(l => l.Cgst));
        entries.Credit(map.OutputSgst, sale.Lines.Sum(l => l.Sgst));
        entries.Credit(map.RoundOff, sale.RoundOff);

        // The reversal needs a reference of its own: with the bill's, Tally would alter the sale into the credit note.
        return sale.IsCancellation
            ? entries.Reversed().ToVoucher(CreditNoteType, sale.Date, CancellationNumber(sale.BillNumber), $"Cancellation of bill {sale.BillNumber}", CancellationNumber(sale.BillNumber))
            : entries.ToVoucher(SalesType, sale.Date, sale.BillNumber, $"Bill {sale.BillNumber}", sale.BillNumber);
    }

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
        var entries = new EntryList();
        foreach (var mode in receipt.Payments.GroupBy(p => p.Mode))
        {
            entries.Debit(map.ModeLedger(mode.Key), mode.Sum(p => p.Amount));
        }

        var counterLedger = receipt.Purpose switch
        {
            ReceiptPurpose.Deposit or ReceiptPurpose.Advance => map.PatientAdvances,
            ReceiptPurpose.PayerSettlement => map.PayerLedger(receipt.PayerName),
            _ => map.PatientDebtors,
        };
        entries.Credit(counterLedger, receipt.Payments.Sum(p => p.Amount));

        var isAdjustment = receipt.Payments.Count > 0 && receipt.Payments.All(p => p.Mode == PaymentMode.Advance);
        return isAdjustment
            ? entries.ToVoucher(JournalType, receipt.Date, receipt.Number, $"Advance adjusted, receipt {receipt.Number}", receipt.Number)
            : entries.ToVoucher(ReceiptType, receipt.Date, receipt.Number, $"Receipt {receipt.Number}", receipt.Number);
    }

    private static AccountingVoucher? Disallowance(TallyDisallowance disallowance, TallyLedgerMap map)
    {
        var entries = new EntryList();
        entries.Debit(map.InsuranceDisallowances, disallowance.Amount);
        entries.Credit(map.PayerLedger(disallowance.PayerName), disallowance.Amount);
        return entries.ToVoucher(JournalType, disallowance.Date, disallowance.Reference, $"Claim deductions {disallowance.Reference}", disallowance.Reference);
    }

    private static AccountingVoucher? Refund(TallyRefund refund, TallyLedgerMap map)
    {
        var entries = new EntryList();
        if (refund.Mode == PaymentMode.Advance)
        {
            entries.Debit(map.PatientDebtors, refund.Amount);
            entries.Credit(map.PatientAdvances, refund.Amount);
            return entries.ToVoucher(JournalType, refund.Date, refund.Number, $"Advance restored, refund {refund.Number}", refund.Number);
        }

        entries.Debit(refund.Purpose is RefundPurpose.Deposit or RefundPurpose.Advance ? map.PatientAdvances : map.PatientDebtors, refund.Amount);
        entries.Credit(map.ModeLedger(refund.Mode), refund.Amount);
        return entries.ToVoucher(PaymentType, refund.Date, refund.Number, $"Refund {refund.Number}", refund.Number);
    }

    private static AccountingVoucher? CreditNote(TallyCreditNote note, TallyLedgerMap map)
    {
        var entries = new EntryList();
        entries.Debit(note.Kind == CreditNoteKind.WriteOff ? map.WriteOffs : map.CreditNotes, note.Amount);
        entries.Credit(map.PatientDebtors, note.Amount);
        return entries.ToVoucher(CreditNoteType, note.Date, note.Number, $"{(note.Kind == CreditNoteKind.WriteOff ? "Write-off" : "Credit note")} {note.Number}", note.Number);
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
        entries.Credit(map.PayoutBank, payout.Net);
        return entries.ToVoucher(PaymentType, payout.Date, payout.Number, $"Doctor payout {payout.Number}", payout.Number);
    }

    /// <summary>Collects entries, merging repeats of the same ledger/side and turning negative amounts into the opposite side.</summary>
    internal sealed class EntryList
    {
        private readonly List<AccountingEntry> _entries = [];

        public void Debit(string ledger, decimal amount, string? costCentre = null) => Add(ledger, amount, isDebit: true, costCentre);

        public void Credit(string ledger, decimal amount, string? costCentre = null) => Add(ledger, amount, isDebit: false, costCentre);

        public EntryList Reversed()
        {
            var reversed = new EntryList();
            foreach (var entry in _entries)
            {
                reversed.Add(entry.LedgerName, entry.Amount, !entry.IsDebit, entry.CostCentre);
            }

            return reversed;
        }

        public AccountingVoucher? ToVoucher(string type, DateOnly date, string number, string narration, string reference) =>
            _entries.Count == 0 ? null : new AccountingVoucher(type, date, number, narration, _entries.ToList(), reference);

        private void Add(string ledger, decimal amount, bool isDebit, string? costCentre)
        {
            amount = Money.Round(amount);
            if (amount == 0)
            {
                return;
            }

            if (amount < 0)
            {
                amount = -amount;
                isDebit = !isDebit;
            }

            var index = _entries.FindIndex(e => e.LedgerName == ledger && e.IsDebit == isDebit && e.CostCentre == costCentre);
            if (index >= 0)
            {
                _entries[index] = _entries[index] with { Amount = _entries[index].Amount + amount };
            }
            else
            {
                _entries.Add(new AccountingEntry(ledger, amount, isDebit, costCentre));
            }
        }
    }
}
