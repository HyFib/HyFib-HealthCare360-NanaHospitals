using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;

/// <summary>/api/v1/cssd/loads — sterilizer cycles: load, start, complete, release, biological indicator, abort and recall report (CSSD-04/05).</summary>
internal static class SterilizationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cssd/loads").WithTags("CSSD");

        group.MapGet("/", List).RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdProcess);
        group.MapGet("/{id:guid}", async (Guid id, SterilizationService loads, CancellationToken ct) => (await loads.GetAsync(id, ct)).ToOk())
            .RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdProcess);
        group.MapGet("/{id:guid}/recall", async (Guid id, SterilizationService loads, CancellationToken ct) => (await loads.RecallReportAsync(id, ct)).ToOk())
            .RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdRecalls);

        group.MapPost("/", async (CreateLoadRequest r, SterilizationService loads, CancellationToken ct) =>
                (await loads.CreateLoadAsync(r, ct)).ToCreated(l => $"/api/v1/cssd/loads/{l.Id}"))
            .RequirePermission(SupportServicesPermissions.CssdProcess).WithValidation<CreateLoadRequest>();
        group.MapPost("/{id:guid}/start", async (Guid id, SterilizationService loads, CancellationToken ct) => (await loads.StartAsync(id, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CssdProcess);
        group.MapPost("/{id:guid}/complete", async (Guid id, CompleteLoadRequest r, SterilizationService loads, CancellationToken ct) =>
                (await loads.CompleteAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CssdProcess).WithValidation<CompleteLoadRequest>();
        group.MapPost("/{id:guid}/release", async (Guid id, SterilizationService loads, CancellationToken ct) => (await loads.ReleaseAsync(id, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CssdRelease);
        group.MapPost("/{id:guid}/biological-indicator", async (Guid id, BiologicalIndicatorRequest r, SterilizationService loads, CancellationToken ct) =>
                (await loads.RecordBiologicalIndicatorAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CssdRelease).WithValidation<BiologicalIndicatorRequest>();
        group.MapPost("/{id:guid}/abort", async (Guid id, FailLoadRequest r, SterilizationService loads, CancellationToken ct) =>
                (await loads.AbortAsync(id, r.Reason, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CssdProcess).WithValidation<FailLoadRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, LoadStatus? status, SterilizationMethod? method, bool? biologicalPending, DateOnly? from, DateOnly? to,
        HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var loads = db.Set<SterilizationLoad>().AsNoTracking();
        if (status is not null)
        {
            loads = loads.Where(l => l.Status == status);
        }

        if (method is not null)
        {
            loads = loads.Where(l => l.Method == method);
        }

        if (biologicalPending == true)
        {
            loads = loads.Where(l => l.BiologicalIndicator == IndicatorResult.Pending
                                     && (l.Status == LoadStatus.Completed || l.Status == LoadStatus.Released));
        }

        if (from is { } fromDate)
        {
            var start = IndiaClock.StartOfDayUtc(fromDate);
            loads = loads.Where(l => l.LoadedAt >= start);
        }

        if (to is { } toDate)
        {
            var end = IndiaClock.StartOfDayUtc(toDate.AddDays(1));
            loads = loads.Where(l => l.LoadedAt < end);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            loads = loads.Where(l => EF.Functions.ILike(l.CycleNumber, like) || EF.Functions.ILike(l.SterilizerName, like));
        }

        var result = await loads.OrderByDescending(l => l.LoadedAt)
            .Select(l => new LoadListItem(
                l.Id, l.CycleNumber, l.SterilizerName, l.Method, l.Status, l.LoadedAt, l.CompletedAt, l.ChemicalIndicator, l.BiologicalIndicator,
                l.ReleasedAt, l.FailureReason, l.ContainsImplants, l.Packs.Count))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }
}
