using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.KpiTargets;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// KPI targets of the signed-in facility: listed with the KPI catalogue, set (a first save creates, later ones send the
/// version they loaded) and removed by administrators holding <see cref="AnalyticsPermissions.KpiTargetsManage"/>. Every
/// change is in the data audit trail with the values before and after, as for any auditable record.
/// </summary>
internal sealed class KpiTargetService(HospitalDbContext db)
{
    public async Task<Result<KpiTargetsDto>> ListAsync(ReportActor actor, CancellationToken ct)
    {
        if (actor.FacilityId is not { } facilityId)
        {
            return NoFacility();
        }

        var facility = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Name).FirstOrDefaultAsync(ct) ?? "This facility";
        var targets = await db.Set<KpiTarget>().AsNoTracking().ToListAsync(ct);
        var byKey = targets.ToDictionary(t => t.KpiKey, StringComparer.Ordinal);
        var roles = await db.Set<Role>().AsNoTracking().Select(r => r.Name).ToListAsync(ct);
        return new KpiTargetsDto(
            facility,
            roles.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            KpiDefinitions.All.Select(k => new KpiTargetItem(
                k.Key, k.Label, k.Unit, k.HigherIsBetter, KpiTargetMath.IsAdditive(k.Unit), k.Formula,
                byKey.TryGetValue(k.Key, out var t) ? ToDto(t) : null)).ToList());
    }

    public async Task<Result<KpiTargetItem>> SaveAsync(ReportActor actor, string key, SaveKpiTargetRequest request, CancellationToken ct)
    {
        if (actor.FacilityId is not { } facilityId)
        {
            return NoFacility();
        }

        if (KpiDefinitions.All.FirstOrDefault(k => k.Key == key) is not { } kpi)
        {
            return Error.NotFound("KPI", key);
        }

        if (KpiTargetMath.CheckOrder(request.Target, request.AmberThreshold, request.RedThreshold, kpi.HigherIsBetter) is { } problem)
        {
            return Error.Field(problem.Field, problem.Message);
        }

        var role = request.OwnerRole.Trim();
        var roleName = await db.Set<Role>().AsNoTracking().Where(r => r.Name == role).Select(r => r.Name).FirstOrDefaultAsync(ct);
        if (roleName is null)
        {
            return Error.Field("ownerRole", "Choose a role from the list.");
        }

        var target = await db.Set<KpiTarget>().FirstOrDefaultAsync(t => t.KpiKey == kpi.Key, ct);
        var changed = target is null || target.Target != request.Target || target.AmberThreshold != request.AmberThreshold || target.RedThreshold != request.RedThreshold;
        if (target is null)
        {
            target = new KpiTarget { FacilityId = facilityId, KpiKey = kpi.Key };
            db.Add(target);
        }

        target.Target = request.Target;
        target.AmberThreshold = request.AmberThreshold;
        target.RedThreshold = request.RedThreshold;
        target.OwnerRole = roleName;
        target.SetByName = actor.DisplayName;
        if (changed)
        {
            // New thresholds start afresh: the owner is told at the next check if the KPI misses them.
            target.LastStatus = null;
        }

        try
        {
            db.SetExpectedVersion(target, request.Version);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.ConcurrencyConflict;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Error.ConcurrencyConflict;
        }

        return new KpiTargetItem(kpi.Key, kpi.Label, kpi.Unit, kpi.HigherIsBetter, KpiTargetMath.IsAdditive(kpi.Unit), kpi.Formula, ToDto(target));
    }

    public async Task<Result> DeleteAsync(ReportActor actor, string key, uint? version, CancellationToken ct)
    {
        if (actor.FacilityId is null)
        {
            return NoFacility();
        }

        var target = await db.Set<KpiTarget>().FirstOrDefaultAsync(t => t.KpiKey == key, ct);
        if (target is null)
        {
            return Error.NotFound("KPI target", key);
        }

        if (version is { } expected)
        {
            db.SetExpectedVersion(target, expected);
        }

        db.Remove(target);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.ConcurrencyConflict;
        }

        return Result.Success();
    }

    /// <summary>The facility's targets by KPI key, read whichever facility the user is signed in to (a report's facility).</summary>
    public static async Task<Dictionary<string, KpiTarget>> ForFacilityAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct) =>
        (await db.Set<KpiTarget>().AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.FacilityId == facilityId && !t.IsDeleted)
            .ToListAsync(ct))
        .ToDictionary(t => t.KpiKey, StringComparer.Ordinal);

    private static KpiTargetDto ToDto(KpiTarget t) =>
        new(t.Target, t.AmberThreshold, t.RedThreshold, t.OwnerRole, t.SetByName, t.UpdatedAt ?? t.CreatedAt, t.LastStatus, t.LastValue, t.LastCheckedAt, t.Version);

    private static Error NoFacility() => Error.Rule("analytics.kpi_targets_facility", "Sign in to a facility to set its KPI targets.");
}
