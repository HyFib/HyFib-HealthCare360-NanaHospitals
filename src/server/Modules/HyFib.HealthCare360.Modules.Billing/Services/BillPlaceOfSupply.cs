using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The place of supply of a bill's services (IGST Act s.12): the state of a payer registered for GST, or else the
/// facility's own. A payer registered in another state is billed IGST instead of CGST and SGST.
/// </summary>
internal static class BillPlaceOfSupply
{
    public static async Task ApplyAsync(HospitalDbContext db, Bill bill, CancellationToken ct)
    {
        var facilityGstin = await db.Set<Facility>().Where(f => f.Id == bill.FacilityId).Select(f => f.Gstin).FirstOrDefaultAsync(ct);
        var payerGstin = bill.PayerId is { } payerId
            ? await db.Set<Payer>().Where(p => p.Id == payerId).Select(p => p.Gstin).FirstOrDefaultAsync(ct)
            : null;
        bill.SetPlaceOfSupply(Gstin.StateCode(facilityGstin), Gstin.StateCode(payerGstin));
    }
}
