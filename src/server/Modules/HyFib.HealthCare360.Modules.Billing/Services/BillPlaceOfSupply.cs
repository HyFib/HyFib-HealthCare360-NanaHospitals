using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The place of supply of a bill (IGST Act 2017): the facility's own state, as the state code of its GSTIN, whoever pays.
/// Health care is supplied where it is performed (s.12(4)); a room, and any service tied to the hospital's premises, where
/// the hospital stands (s.12(3)); and the medicines and devices used in treatment are handed over there. Every bill is
/// therefore an intra-state supply, taxed CGST and SGST, even to a payer registered in another state: the payer's GSTIN
/// names it as the buyer on the e-invoice and in GSTR-1, and does not move the place of supply. The pharmacy's own
/// invoices are not bills: goods it sends to another state are supplied there (s.10), and it keeps its own rule.
/// </summary>
internal static class BillPlaceOfSupply
{
    public static async Task ApplyAsync(HospitalDbContext db, Bill bill, CancellationToken ct) =>
        bill.SetPlaceOfSupply(await FacilityStateAsync(db, bill.FacilityId, ct));

    /// <summary>The GST state code of the facility's GSTIN ("33"); null while it has none.</summary>
    public static async Task<string?> FacilityStateAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct) =>
        Gstin.StateCode(await db.Set<Facility>().Where(f => f.Id == facilityId).Select(f => f.Gstin).FirstOrDefaultAsync(ct));
}
