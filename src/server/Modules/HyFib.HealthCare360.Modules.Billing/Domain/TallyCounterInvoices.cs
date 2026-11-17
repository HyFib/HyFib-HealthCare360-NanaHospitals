using System.Globalization;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// The pharmacy's day invoice by invoice (INT-TALLY, T17: "one voucher per invoice"), from its
/// <see cref="ICounterInvoiceSource"/>:
/// <list type="bullet">
/// <item>each invoice a sales voucher of the pharmacy's voucher type (<see cref="TallyVoucherTypes.Pharmacy"/>), numbered
/// and referenced by the invoice: the money taken with it by mode, and what went on the customer's account, against income
/// by GST rate, output GST and round-off;</item>
/// <item>each return a Credit Note numbered by the return: the reverse, the money paid back and what was set against the
/// balance due;</item>
/// <item>each invoice's balances paid on a day a Receipt (<see cref="CollectionNumber"/>).</item>
/// </list>
/// Narrations name the patient (<see cref="TallyVoucherBuilder.Narrate"/>), or a walk-in customer by the name given at
/// the counter. The counter's own ledgers and its <c>include</c> switch apply as to its summary, which it replaces.
/// </summary>
public static class TallyCounterInvoices
{
    /// <summary>
    /// Whether the counter's day goes invoice by invoice instead of as its summary: the pharmacy, while
    /// <see cref="TallyLedgerMap.PharmacyPerInvoice"/> is on and its invoices were read.
    /// </summary>
    public static bool PerInvoice(TallyDayBook day, TallyLedgerMap map, string counter) =>
        string.Equals(counter, TallyCounters.Pharmacy, StringComparison.OrdinalIgnoreCase) && map.PharmacyPerInvoice
        && day.CounterInvoices.Any(c => string.Equals(c.Counter, counter, StringComparison.OrdinalIgnoreCase));

    /// <summary>The number (and reference) of the receipt of an invoice's balances paid on a day: the invoice, then the day (NHPH260000001/20260925).</summary>
    public static string CollectionNumber(string invoiceNumber, DateOnly date) =>
        invoiceNumber.Trim() + "/" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static IEnumerable<AccountingVoucher?> Vouchers(TallyDayBook day, TallyLedgerMap map)
    {
        foreach (var counter in day.CounterInvoices.Where(c => !c.IsEmpty).OrderBy(c => c.Counter, StringComparer.Ordinal))
        {
            var ledgers = TallyCounters.Ledgers(map, counter.Counter);
            if (!ledgers.Include || !PerInvoice(day, map, counter.Counter))
            {
                continue;
            }

            var name = TallyCounters.NameOf(counter.Counter).ToLowerInvariant();
            foreach (var invoice in counter.Invoices)
            {
                var entries = new TallyVoucherBuilder.EntryList();
                Money(entries, invoice.Payments, ledgers, map, day.Date);
                entries.Debit(ledgers.OnAccount!, invoice.OnAccount);
                Income(entries, invoice.Tax, invoice.RoundOff, ledgers, map);
                yield return entries.ToVoucher(
                    map.VoucherTypes.Pharmacy, day.Date, invoice.Number,
                    TallyVoucherBuilder.Narrate(Patient(day, invoice.PatientId, invoice.CustomerName), map, $"{TallyCounters.NameOf(counter.Counter)} invoice {invoice.Number}"),
                    invoice.Number);
            }

            foreach (var credit in counter.Returns)
            {
                // The sale turned round: what was paid back leaves its mode ledgers, the rest comes off the account.
                var entries = new TallyVoucherBuilder.EntryList();
                Money(entries, credit.PaidBack, ledgers, map, day.Date);
                entries.Debit(ledgers.OnAccount!, credit.AgainstDue);
                Income(entries, credit.Tax, 0m, ledgers, map);
                yield return entries.Reversed().ToVoucher(
                    TallyVoucherBuilder.CreditNoteType, day.Date, credit.Number,
                    TallyVoucherBuilder.Narrate(Patient(day, credit.PatientId, credit.CustomerName), map, $"Return {credit.Number} of {name} invoice {credit.InvoiceNumber}"),
                    credit.Number);
            }

            foreach (var collection in counter.Collections)
            {
                var entries = new TallyVoucherBuilder.EntryList();
                Money(entries, collection.Payments, ledgers, map, day.Date);
                entries.Credit(ledgers.OnAccount!, collection.Amount);
                var number = CollectionNumber(collection.InvoiceNumber, day.Date);
                yield return entries.ToVoucher(
                    TallyVoucherBuilder.ReceiptType, day.Date, number,
                    TallyVoucherBuilder.Narrate(Patient(day, collection.PatientId, collection.CustomerName), map, $"Balance of {name} invoice {collection.InvoiceNumber} collected"),
                    number);
            }
        }
    }

    /// <summary>The registered patient by id, or a walk-in customer by the name given at the counter.</summary>
    private static TallyPatient? Patient(TallyDayBook day, Guid? patientId, string? customerName) =>
        patientId is { } id ? day.Patients.GetValueOrDefault(id)
        : string.IsNullOrWhiteSpace(customerName) ? null
        : new TallyPatient(null, customerName);

    /// <summary>
    /// The money by mode, debited to the counter's own ledger for the mode or the hospital's. A line on a bank ledger
    /// carries each payment's reference: a UPI payment's as an e-fund transfer, a card's RRN as "Others".
    /// </summary>
    private static void Money(TallyVoucherBuilder.EntryList entries, IReadOnlyList<CounterPayment> payments, TallyCounterLedgers ledgers, TallyLedgerMap map, DateOnly date)
    {
        foreach (var mode in payments.GroupBy(p => p.Mode).OrderBy(g => g.Key))
        {
            var ledger = TallyCounters.ModeLedger(ledgers, map, mode.Key);
            entries.Debit(ledger, mode.Sum(p => p.Amount),
                bank: map.IsBankLedger(ledger)
                    ? mode.Select(p => new AccountingBankAllocation(
                        date, p.Mode == CounterPaymentMode.Upi ? AccountingBankTransaction.EFundTransfer : AccountingBankTransaction.Others, p.Amount,
                        string.IsNullOrWhiteSpace(p.Reference) ? null : p.Reference.Trim())).ToList()
                    : null);
        }
    }

    /// <summary>Income by GST rate, output GST and the round-off, credited.</summary>
    private static void Income(TallyVoucherBuilder.EntryList entries, IReadOnlyList<CounterTaxLine> tax, decimal roundOff, TallyCounterLedgers ledgers, TallyLedgerMap map)
    {
        foreach (var rate in tax.GroupBy(t => t.GstRate).OrderBy(g => g.Key))
        {
            entries.Credit(TallyCounters.SalesLedger(ledgers, map, rate.Key), rate.Sum(t => t.Taxable));
        }

        entries.Credit(map.OutputCgst, tax.Sum(t => t.Cgst));
        entries.Credit(map.OutputSgst, tax.Sum(t => t.Sgst));
        entries.Credit(map.OutputIgst, tax.Sum(t => t.Igst));
        entries.Credit(map.RoundOff, roundOff);
    }
}
