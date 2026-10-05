using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// Ledgers of a counter that bills its own sales (setting <c>billing.tally.ledgers</c>, under <c>pharmacy</c> and
/// <c>canteen</c>). Anything not set falls back to the built-in names, and money goes to the hospital's mode ledgers
/// unless the counter has its own (a separate pharmacy cash ledger, for example).
/// </summary>
public sealed record TallyCounterLedgers
{
    /// <summary>Put the counter's day in the day book. Switch off only while its sales are still entered in Tally by hand.</summary>
    public bool Include { get; init; } = true;

    /// <summary>Income ledger ("Pharmacy Sales"); one per GST rate when <see cref="TallyLedgerMap.SalesLedgerPerGstRate"/> is on.</summary>
    public string? Sales { get; init; }

    /// <summary>Ledger for what customers owe: pharmacy credit, or canteen staff credit recovered from salaries.</summary>
    public string? OnAccount { get; init; }

    /// <summary>Payment mode (Cash, Card, Upi) → the counter's own cash or bank ledger.</summary>
    public Dictionary<string, string> ModeLedgers { get; init; } = new();
}

/// <summary>
/// The pharmacy's and the canteen's days in the day book (INT-TALLY). Each counter day gives up to three summary vouchers,
/// numbered by counter, facility and date so a re-post alters them: a Sales voucher (money by mode and what went on
/// account, against income by GST rate, output CGST/SGST and round-off), a Credit Note for returns and cancellations
/// (the reverse), and a Receipt for balances paid later. The invoices themselves stay in their modules' registers.
/// </summary>
public static class TallyCounters
{
    public const string Pharmacy = "pharmacy";
    public const string Canteen = "canteen";

    private static readonly Dictionary<string, (string Code, string Name, string Sales, string OnAccount)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [Pharmacy] = ("PH", "Pharmacy", "Pharmacy Sales", "Pharmacy Receivables"),
        [Canteen] = ("CT", "Canteen", "Canteen Sales", "Staff Canteen Recoverable"),
    };

    public static string NameOf(string counter) => Known.TryGetValue(counter, out var known) ? known.Name : counter;

    /// <summary>The counter's ledgers from the map, with the built-in names for anything not set.</summary>
    public static TallyCounterLedgers Ledgers(TallyLedgerMap map, string counter)
    {
        var configured = counter.ToLowerInvariant() switch
        {
            Pharmacy => map.Pharmacy,
            Canteen => map.Canteen,
            _ => new TallyCounterLedgers(),
        };
        var known = Known.TryGetValue(counter, out var k) ? k : (Code: counter, Name: counter, Sales: counter + " Sales", OnAccount: counter + " Receivables");
        return configured with
        {
            Sales = string.IsNullOrWhiteSpace(configured.Sales) ? known.Sales : configured.Sales.Trim(),
            OnAccount = string.IsNullOrWhiteSpace(configured.OnAccount) ? known.OnAccount : configured.OnAccount.Trim(),
        };
    }

    /// <summary>The number of a counter's summary voucher: PH-NH-20260925 (sales), …-R (returns), …-C (collections).</summary>
    public static string Number(string counter, string? facilityCode, DateOnly date, string? suffix = null)
    {
        var code = Known.TryGetValue(counter, out var known) ? known.Code : CodeOf(counter) ?? "CTR";
        var facility = CodeOf(facilityCode);
        var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var number = facility is null ? $"{code}-{day}" : $"{code}-{facility}-{day}";
        return suffix is null ? number : number + "-" + suffix;
    }

    public static IEnumerable<AccountingVoucher?> Vouchers(TallyDayBook day, TallyLedgerMap map)
    {
        foreach (var counter in day.Counters.Where(c => !c.IsEmpty).OrderBy(c => c.Counter, StringComparer.Ordinal))
        {
            var ledgers = Ledgers(map, counter.Counter);
            if (!ledgers.Include)
            {
                continue;
            }

            var name = NameOf(counter.Counter);
            var date = day.Date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
            if (counter.Sales.Count > 0)
            {
                var number = Number(counter.Counter, day.FacilityCode, day.Date);
                yield return Documents(counter.Sales, ledgers, map, reverse: false)
                    .ToVoucher(TallyVoucherBuilder.SalesType, day.Date, number, $"{name} sales of {date}: {Count(counter.Sales.Count, "invoice")}", number);
            }

            if (counter.Returns.Count > 0)
            {
                var number = Number(counter.Counter, day.FacilityCode, day.Date, "R");
                yield return Documents(counter.Returns, ledgers, map, reverse: true)
                    .ToVoucher(TallyVoucherBuilder.CreditNoteType, day.Date, number, $"{name} returns and cancellations of {date}: {Count(counter.Returns.Count, "document")}", number);
            }

            var entries = new TallyVoucherBuilder.EntryList();
            foreach (var mode in counter.Collected.GroupBy(c => c.Mode))
            {
                entries.Debit(ModeLedger(ledgers, map, mode.Key), mode.Sum(c => c.Amount));
            }

            entries.Credit(ledgers.OnAccount!, counter.Collected.Sum(c => c.Amount));
            var collected = Number(counter.Counter, day.FacilityCode, day.Date, "C");
            yield return entries.ToVoucher(TallyVoucherBuilder.ReceiptType, day.Date, collected, $"{name} balances collected on {date}", collected);
        }
    }

    /// <summary>A part of the code safe in a voucher number: letters, digits and hyphens, at most 20; null when nothing is left.</summary>
    public static string? CodeOf(string? value)
    {
        var code = new string((value ?? string.Empty).Trim().ToUpperInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(20).ToArray());
        return code.Length == 0 ? null : code;
    }

    /// <summary>Eight hex digits that stay the same for the same text (for a doctor without a code).</summary>
    public static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim().ToUpperInvariant())))[..8];

    private static TallyVoucherBuilder.EntryList Documents(CounterDocuments part, TallyCounterLedgers ledgers, TallyLedgerMap map, bool reverse)
    {
        var entries = new TallyVoucherBuilder.EntryList();
        foreach (var mode in part.Payments.GroupBy(p => p.Mode))
        {
            entries.Debit(ModeLedger(ledgers, map, mode.Key), mode.Sum(p => p.Amount));
        }

        entries.Debit(ledgers.OnAccount!, part.OnAccount);
        foreach (var rate in part.Tax.GroupBy(t => t.GstRate).OrderBy(g => g.Key))
        {
            entries.Credit(map.SalesLedgerPerGstRate ? $"{ledgers.Sales} @ {rate.Key.ToString("0.##", CultureInfo.InvariantCulture)}%" : ledgers.Sales!,
                rate.Sum(t => t.Taxable));
        }

        entries.Credit(map.OutputCgst, part.Tax.Sum(t => t.Cgst));
        entries.Credit(map.OutputSgst, part.Tax.Sum(t => t.Sgst));
        entries.Credit(map.RoundOff, part.RoundOff);
        return reverse ? entries.Reversed() : entries;
    }

    private static string ModeLedger(TallyCounterLedgers ledgers, TallyLedgerMap map, CounterPaymentMode mode) =>
        ledgers.ModeLedgers.FirstOrDefault(kv => string.Equals(kv.Key, mode.ToString(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value)).Value?.Trim()
        ?? map.ModeLedger(mode switch
        {
            CounterPaymentMode.Card => PaymentMode.Card,
            CounterPaymentMode.Upi => PaymentMode.Upi,
            _ => PaymentMode.Cash,
        });

    private static string Count(int count, string noun) => count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");
}

/// <summary>Tidies a ledger map before it is saved: names trimmed, blank department and mode entries dropped.</summary>
public static class TallyLedgerMaps
{
    public static TallyLedgerMap Normalize(TallyLedgerMap map) => map with
    {
        DefaultIncome = map.DefaultIncome.Trim(),
        Discount = map.Discount.Trim(),
        OutputCgst = map.OutputCgst.Trim(),
        OutputSgst = map.OutputSgst.Trim(),
        RoundOff = map.RoundOff.Trim(),
        PatientDebtors = map.PatientDebtors.Trim(),
        PatientAdvances = map.PatientAdvances.Trim(),
        PayerReceivables = map.PayerReceivables.Trim(),
        CreditNotes = map.CreditNotes.Trim(),
        WriteOffs = map.WriteOffs.Trim(),
        InsuranceDisallowances = map.InsuranceDisallowances.Trim(),
        DoctorFeeExpense = map.DoctorFeeExpense.Trim(),
        DoctorPayablePrefix = map.DoctorPayablePrefix.TrimStart(),
        TdsPayable = map.TdsPayable.Trim(),
        DoctorRecoveries = map.DoctorRecoveries.Trim(),
        PayoutBank = map.PayoutBank.Trim(),
        GatewaySettlementBank = map.GatewaySettlementBank.Trim(),
        GatewayCharges = map.GatewayCharges.Trim(),
        GatewayInputGst = map.GatewayInputGst.Trim(),
        IncomeByDepartment = Clean(map.IncomeByDepartment, key => key.ToUpperInvariant()),
        ModeLedgers = Clean(map.ModeLedgers, key => key),
        Pharmacy = Counter(map.Pharmacy),
        Canteen = Counter(map.Canteen),
    };

    private static TallyCounterLedgers Counter(TallyCounterLedgers counter) => counter with
    {
        Sales = string.IsNullOrWhiteSpace(counter.Sales) ? null : counter.Sales.Trim(),
        OnAccount = string.IsNullOrWhiteSpace(counter.OnAccount) ? null : counter.OnAccount.Trim(),
        ModeLedgers = Clean(counter.ModeLedgers, key => key),
    };

    private static Dictionary<string, string> Clean(Dictionary<string, string>? map, Func<string, string> key) =>
        (map ?? [])
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
        .GroupBy(pair => key(pair.Key.Trim()), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);
}
