using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>
/// /api/v1/emr/drugs — drug-master search for prescribers (ERX-01). Doctors hold no inventory permissions, so this
/// returns only what a prescription line needs (no cost, vendor or batch data).
/// </summary>
internal static class DrugSearchEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/emr/drugs", Search).WithTags("E-Prescribing").RequirePermission(ClinicalPermissions.PrescriptionsWrite);

    /// <summary>Active drugs by code, barcode, name, generic or brand (name order, at most 50), with stock in the facility's pharmacies.</summary>
    private static async Task<IResult> Search(string? search, int? take, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var drugs = await DrugSearch.Query(db.Set<Item>().AsNoTracking(), search, take).ToListAsync(ct);
        var stock = user.FacilityId is { } facilityId && drugs.Count > 0
            ? await DrugSearch.PharmacyStockAsync(db, facilityId, drugs.Select(d => d.Id).ToList(), time.Today(), ct)
            : null;
        return TypedResults.Ok(drugs.Select(d => DrugSearch.ToDrug(d, stock?.GetValueOrDefault(d.Id))).ToList());
    }
}
