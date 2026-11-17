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

/// <summary>A supplier's invoice received into stock (a goods receipt posted), as a purchase for the accounts.</summary>
/// <param name="Number">The goods receipt's number: the voucher's number and HC360's reference.</param>
/// <param name="ReceivedOn">The day the goods were received.</param>
/// <param name="VendorGstin">The supplier's GSTIN, when registered.</param>
/// <param name="VendorStateCode">The supplier's GST state code: its GSTIN's, else its address's; null when neither says.</param>
/// <param name="SupplierInvoiceNumber">The supplier's own invoice number.</param>
/// <param name="SupplierInvoiceDate">The supplier's invoice date, when entered.</param>
/// <param name="PharmacyStore">The goods went into a pharmacy store (the facility chooses which stores' purchases reach the accounts).</param>
/// <param name="Tax">Taxable value and input GST by rate.</param>
/// <param name="RoundOff">The invoice's rounding to the rupee.</param>
public sealed record PurchaseEntry(
    string Number, DateOnly ReceivedOn, string VendorName, string? VendorGstin, string? VendorStateCode, string SupplierInvoiceNumber,
    DateOnly? SupplierInvoiceDate, bool PharmacyStore, IReadOnlyList<SupplierTaxLine> Tax, decimal RoundOff)
{
    /// <summary>What the hospital owes the supplier for it: the invoice's total.</summary>
    public decimal Total => Tax.Sum(t => t.Total) + RoundOff;
}

/// <summary>
/// The purchases of a day for the Tally day book (INT-TALLY, N11: pharmacy purchases party ledger wise): the goods
/// receipts posted into stock, by the day the goods were received, as GSTR-3B takes their input credit
/// (<see cref="IGstPurchaseSource"/>). A receipt posted after its day joins that day. Supply chain implements it and
/// Billing reads it, so Billing never references Supply chain. Read in the background, where no facility filter applies:
/// implementations filter by the facility given.
/// </summary>
public interface IPurchaseAccountsSource
{
    Task<IReadOnlyList<PurchaseEntry>> GetDayAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken);
}
