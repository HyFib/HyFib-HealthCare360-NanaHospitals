using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Quality.Features.Downtime;

/// <summary>
/// /api/v1/quality/downtime — the downtime log (NABH IMS: downtime is monitored and its procedure rehearsed): spells of
/// working without HC360 and drills of it, each ended and then closed by signing off the back-entry of its paper records,
/// and the period's figures.
/// </summary>
internal static class DowntimeEndpoints
{
    private const int ListSize = 200;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/downtime").WithTags("Quality").RequirePermission(QualityPermissions.DowntimeManage);

        group.MapGet("/", List);
        group.MapGet("/summary", Summary);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Record).WithValidation<RecordDowntimeRequest>();
        group.MapPost("/{id:guid}/end", End).WithValidation<EndDowntimeRequest>();
        group.MapPost("/{id:guid}/back-entry", SignOff).WithValidation<SignOffBackEntryRequest>();
    }

    /// <summary>The facility's downtime started in the period (the last 12 months by default), newest first.</summary>
    private static async Task<IResult> List(DateOnly? from, DateOnly? to, DowntimeStatus? status, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var period = Period(from, to, time);
        if (period.IsFailure)
        {
            return period.Error!.ToProblem();
        }

        var (since, until) = Bounds(period.Value);
        var query = db.Set<DowntimeEvent>().AsNoTracking().Where(e => e.StartedAt >= since && e.StartedAt < until);
        // The status filter is applied before the limit, so a busy period does not hide what was asked for.
        query = status switch
        {
            DowntimeStatus.Ongoing => query.Where(e => e.EndedAt == null),
            DowntimeStatus.BackEntryPending => query.Where(e => e.EndedAt != null && e.BackEntryCompletedAt == null),
            DowntimeStatus.Closed => query.Where(e => e.BackEntryCompletedAt != null),
            _ => query,
        };
        var events = await query.OrderByDescending(e => e.StartedAt).Take(ListSize).ToListAsync(ct);
        var now = time.GetUtcNow();
        return TypedResults.Ok(events.Select(e => ToDto(e, now)).ToList());
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var e = await db.Set<DowntimeEvent>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? Error.NotFound(nameof(DowntimeEvent), id).ToProblem() : TypedResults.Ok(ToDto(e, time.GetUtcNow()));
    }

    private static async Task<IResult> Record(
        RecordDowntimeRequest request,
        HospitalDbContext db,
        INumberSeries numbers,
        ICurrentUser user,
        IAuditLogger audit,
        TimeProvider time,
        CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var now = time.GetUtcNow();
        var times = DowntimeEvent.CheckTimes(request.StartedAt, request.EndedAt, now);
        if (times.IsFailure)
        {
            return times.Error!.ToProblem();
        }

        var byName = user.DisplayName ?? user.UserName;
        var downtime = new DowntimeEvent
        {
            FacilityId = facilityId,
            Kind = request.Kind,
            Cause = request.Cause,
            Affected = request.Affected.Trim(),
            Description = Clean(request.Description),
            StartedAt = request.StartedAt,
            RecordedByName = byName,
        };
        if (request.EndedAt is { } endedAt)
        {
            var ended = downtime.End(endedAt, request.ActionsTaken, byName, now);
            if (ended.IsFailure)
            {
                return ended.Error!.ToProblem();
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        downtime.Number = await numbers.NextAsync(QualityNumberSeries.Downtime, facilityId, ct);
        db.Add(downtime);
        audit.Add(new AuditEntry(AuditCategories.Data, "quality.downtime_recorded",
            $"{downtime.Number}: {downtime.Kind} downtime ({downtime.Cause}) of {downtime.Affected} from {downtime.StartedAt:yyyy-MM-dd HH:mm} UTC")
        {
            EntityType = nameof(DowntimeEvent),
            EntityId = downtime.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/v1/quality/downtime/{downtime.Id}", ToDto(downtime, now));
    }

    private static async Task<IResult> End(
        Guid id, EndDowntimeRequest request, HospitalDbContext db, ICurrentUser user, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var downtime = await db.Set<DowntimeEvent>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (downtime is null)
        {
            return Error.NotFound(nameof(DowntimeEvent), id).ToProblem();
        }

        db.SetExpectedVersion(downtime, request.Version);
        var now = time.GetUtcNow();
        var ended = downtime.End(request.EndedAt, request.ActionsTaken, user.DisplayName ?? user.UserName, now);
        if (ended.IsFailure)
        {
            return ended.Error!.ToProblem();
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "quality.downtime_ended",
            $"{downtime.Number} ended {downtime.EndedAt:yyyy-MM-dd HH:mm} UTC after {downtime.Minutes(now)} minutes")
        {
            EntityType = nameof(DowntimeEvent),
            EntityId = downtime.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(downtime, now));
    }

    private static async Task<IResult> SignOff(
        Guid id, SignOffBackEntryRequest request, HospitalDbContext db, ICurrentUser user, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var downtime = await db.Set<DowntimeEvent>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (downtime is null)
        {
            return Error.NotFound(nameof(DowntimeEvent), id).ToProblem();
        }

        db.SetExpectedVersion(downtime, request.Version);
        var now = time.GetUtcNow();
        var signed = downtime.SignOffBackEntry(request.PaperRecords, request.Note, user.DisplayName ?? user.UserName, now);
        if (signed.IsFailure)
        {
            return signed.Error!.ToProblem();
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "quality.downtime_back_entry_signed",
            $"{downtime.Number}: back-entry of {downtime.PaperRecords} paper records signed off")
        {
            EntityType = nameof(DowntimeEvent),
            EntityId = downtime.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(downtime, now));
    }

    /// <summary>
    /// The period's figures. Downtime counts in the period it started in; its minutes count only within the period, so
    /// a spell across midnight on the last day does not take more than the period's time.
    /// </summary>
    private static async Task<IResult> Summary(DateOnly? from, DateOnly? to, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var period = Period(from, to, time);
        if (period.IsFailure)
        {
            return period.Error!.ToProblem();
        }

        var (start, end) = period.Value;
        var (since, until) = Bounds(period.Value);
        var now = time.GetUtcNow();
        var events = await db.Set<DowntimeEvent>().AsNoTracking()
            .Where(e => e.StartedAt < until && (e.EndedAt == null || e.EndedAt > since))
            .ToListAsync(ct);
        var started = events.Where(e => e.StartedAt >= since).ToList();
        int MinutesIn(DowntimeEvent e)
        {
            var from = e.StartedAt > since ? e.StartedAt : since;
            var stop = e.EndedAt ?? now;
            var to = stop < until ? stop : until;
            return to > from ? (int)Math.Round((to - from).TotalMinutes) : 0;
        }

        var unplanned = events.Where(e => e.Kind == DowntimeKind.Unplanned).ToList();
        var unplannedMinutes = unplanned.Sum(MinutesIn);
        var periodEnd = until < now ? until : now;
        var periodMinutes = Math.Max(1, (periodEnd - since).TotalMinutes);
        var restored = started.Where(e => e.Kind == DowntimeKind.Unplanned && e.EndedAt is not null).Select(e => e.Minutes(now)).ToList();
        var backEntered = started.Where(e => e.EndedAt is not null && e.BackEntryCompletedAt is not null)
            .Select(e => (e.BackEntryCompletedAt!.Value - e.EndedAt!.Value).TotalHours)
            .ToList();
        var lastDrill = await db.Set<DowntimeEvent>().AsNoTracking()
            .Where(e => e.Kind == DowntimeKind.Drill)
            .OrderByDescending(e => e.StartedAt)
            .Select(e => (DateTimeOffset?)e.StartedAt)
            .FirstOrDefaultAsync(ct);
        var open = await db.Set<DowntimeEvent>().AsNoTracking()
            .Where(e => e.BackEntryCompletedAt == null)
            .Select(e => e.EndedAt == null)
            .ToListAsync(ct);

        return TypedResults.Ok(new DowntimeSummary(
            start,
            end,
            started.Count(e => e.Kind == DowntimeKind.Unplanned),
            started.Count(e => e.Kind == DowntimeKind.Planned),
            started.Count(e => e.Kind == DowntimeKind.Drill),
            unplannedMinutes,
            events.Where(e => e.Kind == DowntimeKind.Planned).Sum(MinutesIn),
            unplanned.Select(MinutesIn).DefaultIfEmpty(0).Max(),
            Math.Round(Math.Clamp(100m - (decimal)(unplannedMinutes / periodMinutes * 100), 0m, 100m), 3),
            restored.Count > 0 ? (int)Math.Round(restored.Average()) : null,
            backEntered.Count > 0 ? Math.Round((decimal)backEntered.Average(), 1) : null,
            open.Count(ongoing => ongoing),
            open.Count(ongoing => !ongoing),
            lastDrill));
    }

    private static Result<(DateOnly Start, DateOnly End)> Period(DateOnly? from, DateOnly? to, TimeProvider time)
    {
        var end = to ?? time.LocalToday();
        var start = from ?? end.AddMonths(-12).AddDays(1);
        return end < start || end.DayNumber - start.DayNumber > 3 * 366
            ? Error.Field("to", "Choose a range of at most three years.")
            : (start, end);
    }

    private static (DateTimeOffset Since, DateTimeOffset Until) Bounds((DateOnly Start, DateOnly End) period) =>
        (QualityClock.StartOfDayUtc(period.Start), QualityClock.StartOfDayUtc(period.End.AddDays(1)));

    private static DowntimeEventDto ToDto(DowntimeEvent e, DateTimeOffset now) => new(
        e.Id, e.Number, e.Kind, e.Cause, e.Affected, e.Description, e.StartedAt, e.EndedAt, e.Minutes(now), e.Status, e.RecordedByName,
        e.ActionsTaken, e.EndedByName, e.BackEntryCompletedAt, e.PaperRecords, e.BackEntryNote, e.BackEntrySignedByName, e.Version);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
