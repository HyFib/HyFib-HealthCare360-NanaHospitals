using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;

/// <summary>/api/v1/cssd/wash-cycles — washer-disinfector cycles: start, complete with the printout and inspection, abort (CSSD-04).</summary>
internal static class WashCycleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cssd/wash-cycles").WithTags("CSSD");

        group.MapGet("/", List).RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdProcess);
        group.MapGet("/{id:guid}", async (Guid id, WashCycleService washes, CancellationToken ct) => (await washes.GetAsync(id, ct)).ToOk())
            .RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdProcess);

        group.MapPost("/", async (StartWashCycleRequest r, WashCycleService washes, CancellationToken ct) =>
                (await washes.StartAsync(r, ct)).ToCreated(w => $"/api/v1/cssd/wash-cycles/{w.Id}"))
            .RequirePermission(SupportServicesPermissions.CssdProcess).WithValidation<StartWashCycleRequest>();
        group.MapPost("/{id:guid}/complete", async (Guid id, CompleteWashCycleRequest r, WashCycleService washes, CancellationToken ct) =>
                (await washes.CompleteAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CssdProcess).WithValidation<CompleteWashCycleRequest>();
        group.MapPost("/{id:guid}/abort", async (Guid id, AbortWashCycleRequest r, WashCycleService washes, CancellationToken ct) =>
                (await washes.AbortAsync(id, r.Reason, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CssdProcess).WithValidation<AbortWashCycleRequest>();
    }

    /// <summary>Newest first. <c>setId</c> keeps the cycles that washed that set, as the load dialog offers them.</summary>
    private static async Task<IResult> List(
        [AsParameters] PageQuery query, WashCycleStatus? status, Guid? setId, DateOnly? from, DateOnly? to, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var cycles = db.Set<WashCycle>().AsNoTracking();
        if (status is not null)
        {
            cycles = cycles.Where(c => c.Status == status);
        }

        if (setId is not null)
        {
            cycles = cycles.Where(c => c.Trays.Any(t => t.InstrumentSetId == setId));
        }

        if (from is { } fromDate)
        {
            var start = IndiaClock.StartOfDayUtc(fromDate);
            cycles = cycles.Where(c => c.StartedAt >= start);
        }

        if (to is { } toDate)
        {
            var end = IndiaClock.StartOfDayUtc(toDate.AddDays(1));
            cycles = cycles.Where(c => c.StartedAt < end);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            cycles = cycles.Where(c => EF.Functions.ILike(c.CycleNumber, like) || EF.Functions.ILike(c.WasherName, like));
        }

        var result = await cycles.OrderByDescending(c => c.StartedAt)
            .Select(c => new WashCycleListItem(
                c.Id, c.CycleNumber, c.WasherName, c.Program, c.Status, c.StartedAt, c.CompletedAt, c.A0, c.Trays.Sum(t => t.Quantity),
                c.FailureReason))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }
}
