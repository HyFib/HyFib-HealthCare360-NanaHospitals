using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>The facility (or, for consolidated reports, every facility) a report runs for.</summary>
public sealed record FacilityScope(Guid? FacilityId, string Name, string? LegalName)
{
    public bool IsConsolidated => FacilityId is null;

    /// <summary>The name printed at the top of exports.</summary>
    public string Letterhead => LegalName ?? Name;
}

/// <summary>
/// Which facilities a user may report on. Mirrors sign-in: every active facility unless the user is limited to some
/// (super administrators never are). "All facilities" (a consolidated report) is allowed only to users who can see every
/// active facility, because a consolidated query cannot be narrowed to a subset by the facility filter.
/// </summary>
internal sealed class FacilityAccess(HospitalDbContext db)
{
    public async Task<IReadOnlyList<LookupItem>> AllowedAsync(ReportActor actor, CancellationToken ct)
    {
        var facilities = db.Set<Facility>().AsNoTracking().Where(f => f.IsActive);
        if (!actor.IsSuperUser)
        {
            var assigned = await db.Set<UserFacility>().AsNoTracking().Where(f => f.UserId == actor.UserId).Select(f => f.FacilityId).ToListAsync(ct);
            if (assigned.Count > 0)
            {
                facilities = facilities.Where(f => assigned.Contains(f.Id));
            }
        }

        return await facilities.OrderBy(f => f.Name).Select(f => new LookupItem(f.Id, f.Code, f.Name)).ToListAsync(ct);
    }

    public async Task<bool> CanConsolidateAsync(ReportActor actor, CancellationToken ct)
    {
        var allowed = await AllowedAsync(actor, ct);
        return allowed.Count > 1 && allowed.Count == await db.Set<Facility>().CountAsync(f => f.IsActive, ct);
    }

    public async Task<Result<FacilityScope>> ResolveAsync(ReportActor actor, string? requested, CancellationToken ct)
    {
        if (requested == ReportKeys.AllFacilities)
        {
            if (!await CanConsolidateAsync(actor, ct))
            {
                return Error.Forbidden("analytics.consolidated_denied", "Reports for all facilities together need access to every facility.");
            }

            return new FacilityScope(null, "All facilities", null);
        }

        Guid? target = requested is not null && Guid.TryParse(requested, out var id) ? id : actor.FacilityId;
        if (target is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var allowed = await AllowedAsync(actor, ct);
        if (allowed.All(f => f.Id != facilityId))
        {
            return Error.Forbidden("analytics.facility_denied", "You do not have access to reports for that facility.");
        }

        var facility = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId)
            .Select(f => new FacilityScope(f.Id, f.Name, f.LegalName))
            .FirstAsync(ct);
        return facility;
    }
}

/// <summary>Builds the <see cref="ReportActor"/> for the signed-in user or for a schedule's owner.</summary>
internal sealed class ReportActors(ICurrentUser currentUser, IPermissionService permissions, HospitalDbContext db)
{
    public async Task<ReportActor?> CurrentAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId || currentUser.IsPatient)
        {
            return null;
        }

        var granted = await permissions.GetPermissionsAsync(userId, ct);
        return new ReportActor(userId, currentUser.UserName, currentUser.DisplayName ?? currentUser.UserName ?? "Staff user", currentUser.FacilityId, granted);
    }

    /// <summary>A schedule's owner, if their account is still active; reports then run with the owner's current permissions.</summary>
    public async Task<ReportActor?> ForUserAsync(Guid userId, Guid facilityId, CancellationToken ct)
    {
        var user = await db.Set<User>().AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive && u.Type == UserType.Staff)
            .Select(u => new { u.UserName, u.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (user is null)
        {
            return null;
        }

        permissions.Invalidate(userId);
        var granted = await permissions.GetPermissionsAsync(userId, ct);
        return new ReportActor(userId, user.UserName, user.DisplayName, facilityId, granted);
    }
}
