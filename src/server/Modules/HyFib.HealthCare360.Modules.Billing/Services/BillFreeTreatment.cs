using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Free treatment by law (BNSS 2023, s.397): a bill of a visit the MLC register marks, the victim of a sexual offence or
/// an acid attack, charges nothing (<see cref="Visit.FreeTreatmentReason"/>). Applied wherever a draft's terms are, so a
/// draft made before the visit was marked comes to nothing once it is refreshed or finalized.
/// </summary>
internal static class BillFreeTreatment
{
    public static async Task ApplyAsync(HospitalDbContext db, Bill bill, CancellationToken ct) =>
        bill.SetFreeTreatment(await ReasonAsync(db, bill.VisitId, ct));

    /// <summary>
    /// Why a visit's services are free of cost by law; <c>null</c> when they are billed as usual. Read in any facility, as a
    /// payment callback from the gateway or a terminal has none.
    /// </summary>
    public static async Task<string?> ReasonAsync(HospitalDbContext db, Guid? visitId, CancellationToken ct) =>
        visitId is { } id
            ? await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(v => v.Id == id).Select(v => v.FreeTreatmentReason).FirstOrDefaultAsync(ct)
            : null;
}
