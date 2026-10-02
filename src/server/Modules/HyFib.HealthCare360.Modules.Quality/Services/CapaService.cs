using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Capas;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using CapaEntity = HyFib.HealthCare360.Modules.Quality.Domain.Capa;

namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>
/// CAPA tracking (IRMS-04). Owners start and complete their own actions (with the reporting permission every staff
/// member has); quality managers create, re-plan, verify effectiveness and cancel. Creating a CAPA for an incident
/// under review moves the incident to "CAPA in progress" when the workflow allows it.
/// </summary>
public sealed class CapaService(
    HospitalDbContext db,
    INumberSeries numbers,
    ICurrentUser currentUser,
    IPermissionService permissions,
    INotificationService notifications,
    IncidentService incidents,
    TimeProvider time)
{
    public async Task<Result<Guid>> CreateAsync(CreateCapaRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await IsActiveStaffAsync(request.OwnerUserId, ct))
        {
            return Error.Field("ownerUserId", "Choose an active staff member as owner.");
        }

        Incident? incident = null;
        if (request.IncidentId is { } incidentId)
        {
            incident = await db.Set<Incident>().FirstOrDefaultAsync(i => i.Id == incidentId, ct);
            if (incident is null || incident.Status == IncidentStatus.Closed)
            {
                return Error.Field("incidentId", "Choose an open incident of this facility.");
            }
        }

        if (request.RiskId is { } riskId && !await db.Set<Risk>().AnyAsync(r => r.Id == riskId, ct))
        {
            return Error.Field("riskId", "Choose a risk of this facility.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var capa = new CapaEntity
        {
            FacilityId = facilityId,
            CapaNumber = await numbers.NextAsync(QualityNumberSeries.Capa, facilityId, ct),
            Source = request.Source,
            IncidentId = request.IncidentId,
            RiskId = request.RiskId,
            Type = request.Type,
            Action = request.Action.Trim(),
            OwnerUserId = request.OwnerUserId,
            DueDate = request.DueDate,
            Status = CapaStatus.Open,
        };
        db.Add(capa);
        await db.SaveChangesAsync(ct);

        if (incident is { Status: IncidentStatus.UnderReview or IncidentStatus.Investigation })
        {
            var progress = await incidents.ProgressAsync(incident.Id, ct);
            if (incident.MoveTo(IncidentStatus.CapaInProgress, $"CAPA {capa.CapaNumber} raised", progress, time.GetUtcNow()).IsSuccess)
            {
                await db.SaveChangesAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);

        if (capa.OwnerUserId != currentUser.UserId)
        {
            await notifications.NotifyUsersAsync([capa.OwnerUserId], $"Action {capa.CapaNumber} assigned to you",
                $"{capa.Type} action due {capa.DueDate:dd MMM yyyy}: {Shorten(capa.Action)}", "/quality/capas/mine", "quality", "info", ct);
        }

        return capa.Id;
    }

    public async Task<Result> UpdateAsync(Guid id, UpdateCapaRequest request, CancellationToken ct)
    {
        var capa = await db.Set<CapaEntity>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (capa is null)
        {
            return Error.NotFound(nameof(Capa), id);
        }

        if (!capa.IsOpen && capa.Status != CapaStatus.Completed)
        {
            return Error.Rule("capa.closed", "Verified or cancelled actions cannot be changed.");
        }

        if (!await IsActiveStaffAsync(request.OwnerUserId, ct))
        {
            return Error.Field("ownerUserId", "Choose an active staff member as owner.");
        }

        db.SetExpectedVersion(capa, request.Version);
        var ownerChanged = capa.OwnerUserId != request.OwnerUserId;
        capa.Action = request.Action.Trim();
        capa.OwnerUserId = request.OwnerUserId;
        capa.DueDate = request.DueDate;
        capa.EffectivenessCheckDue = request.EffectivenessCheckDue ?? capa.EffectivenessCheckDue;
        await db.SaveChangesAsync(ct);

        if (ownerChanged && capa.OwnerUserId != currentUser.UserId)
        {
            await notifications.NotifyUsersAsync([capa.OwnerUserId], $"Action {capa.CapaNumber} assigned to you",
                $"Due {capa.DueDate:dd MMM yyyy}: {Shorten(capa.Action)}", "/quality/capas/mine", "quality", "info", ct);
        }

        return Result.Success();
    }

    public Task<Result> StartAsync(Guid id, CancellationToken ct) =>
        OwnerActionAsync(id, (capa, now) => capa.Start(now), ct);

    public async Task<Result> CompleteAsync(Guid id, string note, CancellationToken ct)
    {
        var result = await OwnerActionAsync(id, (capa, now) => capa.Complete(note, now, time.LocalToday()), ct);
        if (result.IsSuccess)
        {
            var capa = await db.Set<CapaEntity>().AsNoTracking().FirstAsync(c => c.Id == id, ct);
            await notifications.NotifyPermissionHoldersAsync(QualityPermissions.CapaManage, capa.FacilityId,
                $"Action {capa.CapaNumber} completed", $"Check its effectiveness by {capa.EffectivenessCheckDue:dd MMM yyyy}.",
                $"/quality/capas/{capa.Id}", "quality", "info", ct);
        }

        return result;
    }

    public async Task<Result> VerifyAsync(Guid id, VerifyCapaRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "Sign in first.");
        }

        var capa = await db.Set<CapaEntity>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (capa is null)
        {
            return Error.NotFound(nameof(Capa), id);
        }

        var result = capa.Verify(request.Effective, request.Note, userId, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        if (!request.Effective)
        {
            await notifications.NotifyUsersAsync([capa.OwnerUserId], $"Action {capa.CapaNumber} was not effective",
                $"Please rework it: {Shorten(request.Note)}", "/quality/capas/mine", "quality", "warning", ct);
        }

        return Result.Success();
    }

    public async Task<Result> CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var capa = await db.Set<CapaEntity>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (capa is null)
        {
            return Error.NotFound(nameof(Capa), id);
        }

        var result = capa.Cancel(reason);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>Owners act on their own actions; quality managers may act on any.</summary>
    private async Task<Result> OwnerActionAsync(Guid id, Func<CapaEntity, DateTimeOffset, Result> action, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "Sign in first.");
        }

        var capa = await db.Set<CapaEntity>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (capa is null)
        {
            return Error.NotFound(nameof(Capa), id);
        }

        var isManager = capa.FacilityId == currentUser.FacilityId && await permissions.HasPermissionAsync(userId, QualityPermissions.CapaManage, ct);
        if (capa.OwnerUserId != userId && !isManager)
        {
            return Error.Forbidden("capa.not_owner", "Only the action owner or the quality team can update this action.");
        }

        var result = action(capa, time.GetUtcNow());
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    private Task<bool> IsActiveStaffAsync(Guid userId, CancellationToken ct) =>
        db.Set<User>().AnyAsync(u => u.Id == userId && u.IsActive && u.Type == UserType.Staff, ct);

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..117] + "…";
}
