using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Quality.Features.Capas;

/// <summary>
/// /api/v1/quality/capas — corrective and preventive actions (IRMS-04). Owners (any staff) see and progress their own
/// actions through the reporting permission; the quality team plans, verifies effectiveness and cancels.
/// </summary>
internal static class CapaEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/capas").WithTags("Quality");

        group.MapGet("/", List).RequirePermission(QualityPermissions.CapaManage);
        group.MapGet("/mine", Mine).RequirePermission(QualityPermissions.IncidentsReport);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(QualityPermissions.CapaManage, QualityPermissions.IncidentsReport);
        group.MapPost("/", async (CreateCapaRequest r, CapaService capas, CancellationToken ct) =>
                (await capas.CreateAsync(r, ct)).ToCreated(id => $"/api/v1/quality/capas/{id}"))
            .RequirePermission(QualityPermissions.CapaManage).WithValidation<CreateCapaRequest>();
        group.MapPut("/{id:guid}", async (Guid id, UpdateCapaRequest r, CapaService capas, CancellationToken ct) => (await capas.UpdateAsync(id, r, ct)).ToNoContent())
            .RequirePermission(QualityPermissions.CapaManage).WithValidation<UpdateCapaRequest>();
        group.MapPost("/{id:guid}/start", async (Guid id, CapaService capas, CancellationToken ct) => (await capas.StartAsync(id, ct)).ToNoContent())
            .RequireAnyPermission(QualityPermissions.CapaManage, QualityPermissions.IncidentsReport);
        group.MapPost("/{id:guid}/complete", async (Guid id, CompleteCapaRequest r, CapaService capas, CancellationToken ct) =>
                (await capas.CompleteAsync(id, r.Note, ct)).ToNoContent())
            .RequireAnyPermission(QualityPermissions.CapaManage, QualityPermissions.IncidentsReport).WithValidation<CompleteCapaRequest>();
        group.MapPost("/{id:guid}/verify", async (Guid id, VerifyCapaRequest r, CapaService capas, CancellationToken ct) => (await capas.VerifyAsync(id, r, ct)).ToNoContent())
            .RequirePermission(QualityPermissions.CapaManage).WithValidation<VerifyCapaRequest>();
        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelCapaRequest r, CapaService capas, CancellationToken ct) => (await capas.CancelAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(QualityPermissions.CapaManage).WithValidation<CancelCapaRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        CapaStatus? status,
        bool? overdueOnly,
        bool? openOnly,
        Guid? ownerUserId,
        Guid? incidentId,
        CapaSource? source,
        HospitalDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.LocalToday();
        var capas = db.Set<Domain.Capa>().AsNoTracking();
        if (status is not null)
        {
            capas = capas.Where(c => c.Status == status);
        }
        else if (openOnly == true || overdueOnly == true)
        {
            capas = capas.Where(c => c.Status == CapaStatus.Open || c.Status == CapaStatus.InProgress);
        }

        if (overdueOnly == true)
        {
            capas = capas.Where(c => c.DueDate < today);
        }

        if (ownerUserId is not null)
        {
            capas = capas.Where(c => c.OwnerUserId == ownerUserId);
        }

        if (incidentId is not null)
        {
            capas = capas.Where(c => c.IncidentId == incidentId);
        }

        if (source is not null)
        {
            capas = capas.Where(c => c.Source == source);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            capas = capas.Where(c => c.CapaNumber == term.ToUpper() || EF.Functions.ILike(c.Action, like, QueryExtensions.LikeEscape));
        }

        var rows = await Project(capas, db, today).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(rows with { Items = await WithIncidentNumbersAsync(db, rows.Items, ct) });
    }

    /// <summary>The caller's own actions, in any facility (owners are often clinicians who move between branches).</summary>
    private static async Task<IResult> Mine(bool? includeClosed, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var capas = db.Set<Domain.Capa>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(c => c.OwnerUserId == user.UserId && (includeClosed == true || c.Status == CapaStatus.Open || c.Status == CapaStatus.InProgress));
        return TypedResults.Ok(await WithIncidentNumbersAsync(db, await Project(capas, db, time.LocalToday()).Take(200).ToListAsync(ct), ct));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, ICurrentUser user, IPermissionService permissions, TimeProvider time, CancellationToken ct)
    {
        var capa = await db.Set<Domain.Capa>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        var isManager = capa is not null && capa.FacilityId == user.FacilityId && user.UserId is { } userId
                        && await permissions.HasPermissionAsync(userId, QualityPermissions.CapaManage, ct);
        if (capa is null || (capa.OwnerUserId != user.UserId && !isManager))
        {
            return Error.NotFound(nameof(Domain.Capa), id).ToProblem();
        }

        var owner = await db.Set<User>().Where(u => u.Id == capa.OwnerUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        return TypedResults.Ok(new CapaDetail(
            capa.Id, capa.CapaNumber, capa.Source, capa.IncidentId, capa.RiskId, capa.Type, capa.Action, capa.OwnerUserId, owner, capa.DueDate, capa.Status,
            capa.IsOverdue(time.LocalToday()), capa.StartedAt, capa.CompletedAt, capa.CompletionNote, capa.EffectivenessCheckDue, capa.IsEffective,
            capa.EffectivenessNote, capa.VerifiedAt, capa.CancelReason, capa.ReworkCount, capa.Version));
    }

    private static IQueryable<CapaListItem> Project(IQueryable<Domain.Capa> capas, HospitalDbContext db, DateOnly today) =>
        from c in capas
        join u in db.Set<User>() on c.OwnerUserId equals u.Id into owners
        from u in owners.DefaultIfEmpty()
        orderby c.Status == CapaStatus.Verified || c.Status == CapaStatus.Cancelled, c.DueDate
        select new CapaListItem(
            c.Id, c.CapaNumber, c.Source, c.IncidentId, null, c.RiskId, c.Type, c.Action, c.OwnerUserId,
            u != null ? u.DisplayName : null, c.DueDate, c.Status,
            (c.Status == CapaStatus.Open || c.Status == CapaStatus.InProgress) && c.DueDate < today, c.EffectivenessCheckDue);

    /// <summary>
    /// The incidents' numbers, from any facility, read apart: EF Core ignores a query filter for the whole query, so reading
    /// them with the actions took the facility filter off the actions too, and the list showed every facility's.
    /// </summary>
    private static async Task<List<CapaListItem>> WithIncidentNumbersAsync(HospitalDbContext db, IReadOnlyList<CapaListItem> capas, CancellationToken ct)
    {
        var ids = capas.Select(c => c.IncidentId).OfType<Guid>().Distinct().ToList();
        var numbers = ids.Count == 0
            ? []
            : await db.Set<Incident>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(i => ids.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.IncidentNumber, ct);
        return [.. capas.Select(c => c.IncidentId is { } incident && numbers.TryGetValue(incident, out var number) ? c with { IncidentNumber = number } : c)];
    }
}
