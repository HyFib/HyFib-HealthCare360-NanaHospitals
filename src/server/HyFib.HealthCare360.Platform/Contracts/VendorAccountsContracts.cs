namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// Taxable value and input GST at one rate of a supplier's document: CGST and SGST from a supplier in the state, IGST from
/// one in another state.
/// </summary>
public sealed record SupplierTaxLine(decimal GstRate, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst = 0)
{
    public decimal Total => Taxable + Cgst + Sgst + Igst;
}

/// <summary>
/// A purchase return posted on the day: what was handed back at cost (<paramref name="Taxable"/>) and the input GST it
/// takes back. The supplier is owed that much less.
/// </summary>
/// <param name="VendorReference">The supplier's own reference for the return, when given.</param>
/// <param name="SupplierInvoiceNumber">
/// The supplier's invoice the goods came on, when the note names its goods receipt: the return is set against that bill
/// on the supplier's ledger.
/// </param>
/// <param name="Tax">The note's taxable value and GST by rate (they add up to its totals); empty when not known.</param>
public sealed record VendorDebitNoteEntry(
    string Number, string VendorName, string? VendorReference, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, string? SupplierInvoiceNumber = null,
    IReadOnlyList<SupplierTaxLine>? Tax = null)
{
    public decimal Total => Taxable + Cgst + Sgst + Igst;
}

/// <summary>A change to what the hospital owes a supplier, posted on the day: <paramref name="Reduce"/> when it owes less.</summary>
public sealed record VendorPayableAdjustmentEntry(string Number, string VendorName, bool Reduce, decimal Amount, string? VendorReference);

/// <summary>One facility's day of supplier documents that reach the accounts.</summary>
public sealed record VendorAccountsDay(IReadOnlyList<VendorDebitNoteEntry> DebitNotes, IReadOnlyList<VendorPayableAdjustmentEntry> Adjustments)
{
    public static VendorAccountsDay None { get; } = new([], []);
}

/// <summary>
/// The supplier documents of a day for the Tally day book (INT-TALLY): purchase returns (debit notes) and payable adjustments,
/// each posted only after a second person approved it. Supply chain implements it and Billing reads it, so Billing never
/// references Supply chain. Read in the background, where no facility filter applies: implementations filter by the facility
/// given and use the hospital's (IST) calendar day.
/// </summary>
public interface IVendorAccountsSource
{
    Task<VendorAccountsDay> GetDayAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken);
}
