using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Encounters;

/// <summary>/api/v1/emr/encounters — start, document (SOAP), sign and amend encounters, and their previous visit (EMR-02..07).</summary>
internal static class EncounterEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr/encounters").WithTags("EMR");

        group.MapPost("/", Start).RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<StartEncounterRequest>();
        group.MapGet("/{id:guid}", Get).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}/previous-visit", PreviousVisit).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPut("/{id:guid}", Update).RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<UpdateEncounterRequest>();
        group.MapPost("/{id:guid}/sign", Sign).RequirePermission(ClinicalPermissions.EncountersSign);
        group.MapPost("/{id:guid}/amend", Amend).RequirePermission(ClinicalPermissions.EncountersSign).WithValidation<AmendEncounterRequest>();
    }

    private static async Task<IResult> Start(StartEncounterRequest request, EncounterService encounters, CancellationToken ct) =>
        (await encounters.StartAsync(request, ct)).ToCreated(e => $"/api/v1/emr/encounters/{e.Id}");

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, ClinicalAccess access, EncounterService encounters, CancellationToken ct)
    {
        var encounter = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(e => e.Amendments)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (encounter is null)
        {
            return Error.NotFound(nameof(Encounter), id).ToProblem();
        }

        var opened = await access.OpenAsync(encounter.PatientId, "Viewed encounter", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await encounters.DetailAsync(encounter, ct)).ToOk();
    }

    /// <summary>The previous visit the workspace shows while this encounter is written (EMR-02..07).</summary>
    private static async Task<IResult> PreviousVisit(Guid id, HospitalDbContext db, ClinicalAccess access, PatientRecordQueries queries, CancellationToken ct)
    {
        var encounter = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (encounter is null)
        {
            return Error.NotFound(nameof(Encounter), id).ToProblem();
        }

        var opened = await access.OpenAsync(encounter.PatientId, "Viewed previous visit", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(new PreviousVisitResponse(await queries.PreviousVisitAsync(encounter, ct)));
    }

    private static async Task<IResult> Update(Guid id, UpdateEncounterRequest request, EncounterService encounters, CancellationToken ct) =>
        (await encounters.UpdateAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Sign(Guid id, SignEncounterRequest request, EncounterService encounters, CancellationToken ct) =>
        (await encounters.SignAsync(id, request.Version, ct)).ToOk();

    private static async Task<IResult> Amend(Guid id, AmendEncounterRequest request, EncounterService encounters, CancellationToken ct) =>
        (await encounters.AmendAsync(id, request, ct)).ToOk();
}
