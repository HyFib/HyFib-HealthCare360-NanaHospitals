using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Quality.Features.Incidents;

/// <summary>/api/v1/quality/incidents/{id}/investigation — root-cause analysis (five whys or fishbone), IRMS-03.</summary>
internal static class InvestigationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/incidents/{id:guid}/investigation").WithTags("Quality").RequirePermission(QualityPermissions.IncidentsManage);

        group.MapGet("/", Get);
        group.MapPut("/", async (Guid id, SaveInvestigationRequest r, IncidentService incidents, CancellationToken ct) =>
                (await incidents.SaveInvestigationAsync(id, r, ct)).ToOk())
            .WithValidation<SaveInvestigationRequest>().FirstSaveCreates();
        group.MapPost("/complete", async (Guid id, IncidentService incidents, CancellationToken ct) =>
            (await incidents.CompleteInvestigationAsync(id, ct)).ToNoContent());
        group.MapPost("/reopen", async (Guid id, IncidentService incidents, CancellationToken ct) =>
            (await incidents.ReopenInvestigationAsync(id, ct)).ToNoContent());
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var investigation = await db.Set<Investigation>().AsNoTracking().Include(i => i.Factors).FirstOrDefaultAsync(i => i.IncidentId == id, ct);
        if (investigation is null)
        {
            return Error.NotFound(nameof(Investigation), id).ToProblem();
        }

        var investigators = await db.Set<User>().AsNoTracking()
            .Where(u => investigation.InvestigatorUserIds.Contains(u.Id))
            .Select(u => new PersonRef(u.Id, u.DisplayName))
            .ToListAsync(ct);

        return TypedResults.Ok(new InvestigationDto(
            investigation.Id, investigation.IncidentId, investigation.Method, investigation.ProblemStatement, investigation.Whys,
            investigation.Factors.OrderBy(f => f.Category).Select(f => new RcaFactorDto(f.Id, f.Category, f.Description, f.IsRootCause)).ToList(),
            investigation.Findings, investigation.Recommendations, investigators, investigation.Status, investigation.StartedAt,
            investigation.CompletedAt, investigation.Version));
    }
}
