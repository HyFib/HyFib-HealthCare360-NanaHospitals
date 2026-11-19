using System.Globalization;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>CGST, SGST and IGST at one rate of a document; <paramref name="Rate"/> is the whole GST rate, null when not known.</summary>
public sealed record TallyGstLine(decimal? Rate, decimal Cgst, decimal Sgst, decimal Igst);

/// <summary>
/// GST in the day book (T13): each tax posted per rate when the facility keeps a ledger per rate
/// (<see cref="TallyLedgerMap.GstLedgerPerRate"/>), and a party's GST details for a B2B sale or a purchase.
/// </summary>
public static class TallyGst
{
    /// <summary>A rate as a ledger name gives it: the invariant culture, no trailing zeros (2.5, 6, 9, 14, 0.125).</summary>
    public static string Percent(decimal rate) => rate.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The state a GST state code (or a GSTIN's first two digits) stands for, as GST names it ("33" is Tamil Nadu); null when it names none.</summary>
    public static string? StateName(string? codeOrGstin) =>
        codeOrGstin?.Trim() is { Length: >= 2 } text && Gstin.States.TryGetValue(text[..2], out var name) ? name : null;

    /// <summary>The GST party of a document: its ledger, its GSTIN and state, and the place of supply (a GST state code).</summary>
    public static AccountingParty Party(string ledger, string? gstin, string? stateCode, string? placeOfSupply) =>
        new(ledger, string.IsNullOrWhiteSpace(gstin) ? null : gstin.Trim().ToUpperInvariant(), StateName(stateCode ?? gstin), StateName(placeOfSupply));

    /// <summary>Output GST (a sale's), credited, or debited when a credit note takes it back.</summary>
    internal static void Output(TallyVoucherBuilder.EntryList entries, TallyLedgerMap map, IEnumerable<TallyGstLine> lines, bool debit = false) =>
        Post(entries, lines, debit, rate => (map.OutputCgstAt(rate), map.OutputSgstAt(rate), map.OutputIgstAt(rate)));

    /// <summary>Input GST (a purchase's), debited, or credited when a purchase return takes it back.</summary>
    internal static void Input(TallyVoucherBuilder.EntryList entries, TallyLedgerMap map, IEnumerable<TallyGstLine> lines, bool debit = true) =>
        Post(entries, lines, debit, rate => (map.InputCgstAt(rate), map.InputSgstAt(rate), map.InputIgstAt(rate)));

    /// <summary>Each tax by rate, lowest rate first; the amounts are the documents' own, so the voucher's total is unchanged.</summary>
    private static void Post(
        TallyVoucherBuilder.EntryList entries, IEnumerable<TallyGstLine> lines, bool debit, Func<decimal?, (string Cgst, string Sgst, string Igst)> ledgers)
    {
        foreach (var rate in lines.GroupBy(l => l.Rate is > 0 ? l.Rate : null).OrderBy(g => g.Key))
        {
            var (cgst, sgst, igst) = ledgers(rate.Key);
            Add(entries, cgst, rate.Sum(l => l.Cgst), debit);
            Add(entries, sgst, rate.Sum(l => l.Sgst), debit);
            Add(entries, igst, rate.Sum(l => l.Igst), debit);
        }
    }

    private static void Add(TallyVoucherBuilder.EntryList entries, string ledger, decimal amount, bool debit)
    {
        if (debit)
        {
            entries.Debit(ledger, amount);
        }
        else
        {
            entries.Credit(ledger, amount);
        }
    }
}
