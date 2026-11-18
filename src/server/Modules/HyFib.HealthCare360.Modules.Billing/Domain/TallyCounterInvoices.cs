using System.Globalization;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// The pharmacy's day invoice by invoice (INT-TALLY, T17: "one voucher per invoice"), from its
/// <see cref="ICounterInvoiceSource"/>:
/// <list type="bullet">
/// <item>each invoice a sales voucher of the pharmacy's voucher type (<see cref="TallyVoucherTypes.Pharmacy"/>), numbered
/// and referenced by the invoice: the money taken with it by mode, and what went on the customer's account, against income
/// and output GST by GST rate, and round-off; a B2B invoice carries its buyer's GST details;</item>
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
                yield return WithBuyer(
                    entries.ToVoucher(
                        map.VoucherTypes.Pharmacy, day.Date, invoice.Number,
                        TallyVoucherBuilder.Narrate(Patient(day, invoice.PatientId, invoice.CustomerName), map, $"{TallyCounters.NameOf(counter.Counter)} invoice {invoice.Number}"),
                        invoice.Number),
                    invoice.Buyer, invoice.OnAccount, invoice.Payments, ledgers, map);
            }

            foreach (var credit in counter.Returns)
            {
                // The sale turned round: what was paid back leaves its mode ledgers, the rest comes off the account.
                var entries = new TallyVoucherBuilder.EntryList();
                Money(entries, credit.PaidBack, ledgers, map, day.Date);
                entries.Debit(ledgers.OnAccount!, credit.AgainstDue);
                Income(entries, credit.Tax, 0m, ledgers, map);
                yield return WithBuyer(
                    entries.Reversed().ToVoucher(
                        TallyVoucherBuilder.CreditNoteType, day.Date, credit.Number,
                        TallyVoucherBuilder.Narrate(Patient(day, credit.PatientId, credit.CustomerName), map, $"Return {credit.Number} of {name} invoice {credit.InvoiceNumber}"),
                        credit.Number),
                    credit.Buyer, credit.AgainstDue, credit.PaidBack, ledgers, map);
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

    /// <summary>
    /// A B2B invoice or return with its buyer's GST details (T13): the buyer's GSTIN, state and the place of supply, and as
    /// the party ledger the one the buyer's side of it is in: the customer account when any of it went on (or came off)
    /// the account, else the ledger of the mode that took (or paid back) most of the money.
    /// </summary>
    private static AccountingVoucher? WithBuyer(
        AccountingVoucher? voucher, CounterBuyer? buyer, decimal onAccount, IReadOnlyList<CounterPayment> money, TallyCounterLedgers ledgers, TallyLedgerMap map)
    {
        if (voucher is null || buyer is null)
        {
            return voucher;
        }

        var ledger = onAccount > 0
            ? ledgers.OnAccount!
            : money.GroupBy(p => p.Mode).OrderByDescending(g => g.Sum(p => p.Amount)).ThenBy(g => g.Key).Select(g => TallyCounters.ModeLedger(ledgers, map, g.Key)).FirstOrDefault()
              ?? ledgers.OnAccount!;
        return voucher with { Party = TallyGst.Party(ledger, buyer.Gstin, null, buyer.PlaceOfSupply) };
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

        TallyGst.Output(entries, map, tax.Select(t => new TallyGstLine(t.GstRate, t.Cgst, t.Sgst, t.Igst)));
        entries.Credit(map.RoundOff, roundOff);
    }
}
