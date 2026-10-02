using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum TallyExportStatus
{
    Queued = 0,
    Posted,

    /// <summary>No accounting exporter is configured; the day book is available as a manual import file.</summary>
    NotConfigured,

    Failed,
}

/// <summary>One facility's day book for one date (INT-TALLY): the job log used for retries and reconciliation.</summary>
[NotAudited]
public sealed class TallyExport : Entity
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

public sealed record TallyDoctorFee(string PractitionerName, decimal Amount);

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
    public bool IsEmpty => Sales.Count + Receipts.Count + Disallowances.Count + Refunds.Count + CreditNotes.Count + DoctorFees.Count + Payouts.Count
        + (GatewaySettlements?.Count ?? 0) == 0;
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
        vouchers.AddRange(day.DoctorFees.Select((f, i) => DoctorFee(f, i + 1, day.Date, map)));
        vouchers.AddRange(day.Payouts.Select(p => Payout(p, map)));
        vouchers.AddRange((day.GatewaySettlements ?? []).Select(s => SettlementVoucher(s, map)));
        return vouchers.OfType<AccountingVoucher>().ToList();
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

        return sale.IsCancellation
            ? entries.Reversed().ToVoucher(CreditNoteType, sale.Date, sale.BillNumber + "/C", $"Cancellation of bill {sale.BillNumber}", sale.BillNumber)
            : entries.ToVoucher(SalesType, sale.Date, sale.BillNumber, $"Bill {sale.BillNumber}", sale.BillNumber);
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

    private static AccountingVoucher? DoctorFee(TallyDoctorFee fee, int sequence, DateOnly date, TallyLedgerMap map)
    {
        var entries = new EntryList();
        entries.Debit(map.DoctorFeeExpense, fee.Amount);
        entries.Credit(map.DoctorPayable(fee.PractitionerName), fee.Amount);
        var number = $"DF-{date:yyyyMMdd}-{sequence:000}";
        return entries.ToVoucher(JournalType, date, number, $"Doctor fees accrued ({fee.PractitionerName})", number);
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
    private sealed class EntryList
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
