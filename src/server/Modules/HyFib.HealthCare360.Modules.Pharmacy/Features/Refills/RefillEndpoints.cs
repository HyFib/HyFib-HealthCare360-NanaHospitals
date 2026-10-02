using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Services;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Refills;

/// <summary>
/// Refills (PH-05, PP-05): staff queue at /api/v1/pharmacy/refills and the patient portal at /api/v1/portal/refills
/// (always scoped to the patients linked to the signed-in mobile number).
/// </summary>
internal static class RefillEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pharmacy/refills").WithTags("Pharmacy");

        group.MapGet("/", List).RequirePermission(PharmacyPermissions.Refills);
        group.MapPost("/", async (CreateRefillRequest r, RefillService refills, CancellationToken ct) =>
                (await refills.RequestAsync(r, RefillChannel.Staff, ct)).ToCreated(d => $"/api/v1/pharmacy/refills/{d.Id}"))
            .RequirePermission(PharmacyPermissions.Refills).WithValidation<CreateRefillRequest>();
        group.MapPost("/{id:guid}/approve", async (Guid id, RefillService refills, CancellationToken ct) =>
                (await refills.ApproveAsync(id, ct)).ToHttpResult(entryId => TypedResults.Ok(new { queueEntryId = entryId })))
            .RequirePermission(PharmacyPermissions.Refills);
        group.MapPost("/{id:guid}/reject", async (Guid id, PharmacyReasonRequest r, RefillService refills, CancellationToken ct) =>
                (await refills.RejectAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(PharmacyPermissions.Refills).WithValidation<PharmacyReasonRequest>();

        var portal = app.MapGroup("/api/v1/portal/refills").WithTags("Patient Portal").RequireFeature(FeatureKeys.PatientPortal);
        portal.MapGet("/", async (RefillService refills, CancellationToken ct) => TypedResults.Ok(await refills.PortalViewAsync(ct)))
            .RequirePermission(PatientsPermissions.Portal);
        portal.MapPost("/", async (CreateRefillRequest r, RefillService refills, CancellationToken ct) =>
                (await refills.RequestAsync(r, RefillChannel.Portal, ct)).ToCreated(d => "/api/v1/portal/refills"))
            .RequirePermission(PatientsPermissions.Portal).WithValidation<CreateRefillRequest>();
    }

    private static async Task<IResult> List([AsParameters] PageQuery query, RefillStatus? status, HospitalDbContext db, RefillService refills, CancellationToken ct)
    {
        var page = query.ToRequest();
        var requests = db.Set<RefillRequest>().AsNoTracking();
        requests = status is null
            ? requests.Where(r => r.Status == RefillStatus.Requested)
            : requests.Where(r => r.Status == status);
        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            requests = requests.Where(r => r.Number == upper);
        }

        return TypedResults.Ok(await refills.Requests(requests).ToPagedResultAsync(page, ct));
    }
}
