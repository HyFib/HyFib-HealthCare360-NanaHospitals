using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>The pre-downtime pack: what the hospital prints to keep caring for patients while HC360 is down.</summary>
/// <param name="Kind">Printed now, or the one HC360 made last night.</param>
public sealed record DowntimePack(
    Guid Id,
    DowntimePackKind Kind,
    DateTimeOffset GeneratedAt,
    DateTimeOffset WindowEndsAt,
    string FacilityName,
    string? GeneratedByName,
    int Patients,
    IReadOnlyList<DowntimePackSection> Sections);

/// <summary>
/// Builds the pre-downtime pack from every module's part (<see cref="IDowntimePackSource"/>), on demand and each night,
/// and keeps it (<see cref="DowntimePackSnapshot"/>) so the latest one can be read offline. Every print is audited.
/// </summary>
public sealed class DowntimePackService(
    HospitalDbContext db,
    IEnumerable<IDowntimePackSource> sources,
    IPermissionService permissions,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>The pack covers the doses, orders and operations due in the next 24 hours.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Prints the pack now for the signed-in user's facility, keeps it, and audits the print.</summary>
    public async Task<Result<DowntimePack>> PrintAsync(CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var canSeeRestricted = currentUser.UserId is { } userId && await permissions.HasPermissionAsync(userId, PatientsPermissions.ReadRestricted, ct);
        var pack = await BuildAsync(facilityId, DowntimePackKind.OnDemand, currentUser.DisplayName ?? currentUser.UserName, canSeeRestricted, ct);
        Keep(pack);
        audit.Add(new AuditEntry(AuditCategories.PhiAccess, "patients.downtime_pack_printed",
            $"Printed the pre-downtime pack ({pack.Patients} in-patients)")
        {
            EntityType = nameof(DowntimePackSnapshot),
            EntityId = pack.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return pack;
    }

    /// <summary>The latest pack kept for the facility (the nightly one, or a later print), audited as it is read.</summary>
    public async Task<Result<DowntimePack>> LatestAsync(CancellationToken ct)
    {
        var snapshot = await db.Set<DowntimePackSnapshot>().AsNoTracking()
            .Where(s => s.FacilityId == currentUser.FacilityId)
            .OrderByDescending(s => s.GeneratedAt)
            .FirstOrDefaultAsync(ct);
        if (snapshot is null)
        {
            return new Error("downtime.pack_none", "No pack has been made for this facility yet. Print one now.", ErrorType.NotFound);
        }

        var pack = JsonSerializer.Deserialize<DowntimePack>(snapshot.Content, Json)!;
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "patients.downtime_pack_read",
            $"Read the {snapshot.Kind.ToString().ToLowerInvariant()} pre-downtime pack of {Local(snapshot.GeneratedAt)}")
        {
            EntityType = nameof(DowntimePackSnapshot),
            EntityId = snapshot.Id.ToString(),
        }, ct);
        return pack;
    }

    /// <summary>Builds a pack for <paramref name="facilityId"/> (not kept: <see cref="Keep"/>).</summary>
    public async Task<DowntimePack> BuildAsync(Guid facilityId, DowntimePackKind kind, string? generatedBy, bool canSeeRestricted, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var context = new DowntimePackContext(facilityId, now, now + Window, canSeeRestricted);
        var sections = new List<DowntimePackSection>();
        foreach (var source in sources)
        {
            sections.AddRange(await source.BuildAsync(context, ct));
        }

        var facilityName = await db.Set<Facility>().AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.Id == facilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        var patients = sections.FirstOrDefault(s => s.Key == "census")?.Tables.Sum(t => t.Rows.Count) ?? 0;
        return new DowntimePack(Guid.CreateVersion7(), kind, now, now + Window, facilityName ?? string.Empty, generatedBy, patients,
            [.. sections.OrderBy(s => DowntimePackOrder.Of(s.Key))]);
    }

    /// <summary>Keeps <paramref name="pack"/> (saved with the caller's changes), and drops those older than a week.</summary>
    public void Keep(DowntimePack pack, Guid? facilityId = null)
    {
        db.Add(new DowntimePackSnapshot
        {
            Id = pack.Id,
            FacilityId = facilityId ?? currentUser.FacilityId ?? throw new InvalidOperationException("No facility for the pack."),
            Kind = pack.Kind,
            GeneratedAt = pack.GeneratedAt,
            GeneratedByName = pack.GeneratedByName,
            Patients = pack.Patients,
            Content = JsonSerializer.Serialize(pack, Json),
        });
    }

    internal async Task PurgeAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var before = now - DowntimePackSnapshot.KeptFor;
        var refreshedBefore = now - DowntimePackSnapshot.RefreshedKeptFor;
        await db.Set<DowntimePackSnapshot>().IgnoreQueryFilters()
            .Where(s => s.GeneratedAt < before || (s.Kind == DowntimePackKind.Refreshed && s.GeneratedAt < refreshedBefore))
            .ExecuteDeleteAsync(ct);
    }

    private static string Local(DateTimeOffset at) => NumberFormat.ToHospitalTime(at).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// When each facility's pack is due, decided apart from the clock and the database so it can be tested: the nightly pack
/// once a day after <see cref="DowntimePackJob.RunAfterHour"/> (hospital time) and again a little later if it failed, and
/// at any hour a refreshed one every <c>downtime.pack.refreshMinutes</c>. The offline copy labels the ward sheets and
/// medicines with the pack's own time, so a pack kept current is what keeps that label short.
/// </summary>
internal sealed class DowntimePackSchedule
{
    /// <summary>The refresh period when the setting is not set.</summary>
    public const int DefaultRefreshMinutes = 30;

    /// <summary>Setting: minutes between the refreshed packs, made at any hour; 0 means the nightly pack only.</summary>
    public const string RefreshMinutesSetting = "downtime.pack.refreshMinutes";

    /// <summary>A facility whose pack failed is tried again after this long, not on every tick.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly Dictionary<Guid, DateOnly> _nightlyDone = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastMade = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastFailed = [];

    /// <summary>The setting's value as minutes: unset is <see cref="DefaultRefreshMinutes"/>, below 0 is 0, and a day is the most.</summary>
    public static int RefreshMinutes(int? setting) => setting is null ? DefaultRefreshMinutes : Math.Clamp(setting.Value, 0, 24 * 60);

    /// <summary>
    /// What to make for <paramref name="facilityId"/> now, or null: the nightly pack, a refreshed one, or nothing. Only the
    /// nightly pack waits for <see cref="DowntimePackJob.RunAfterHour"/>: the wards give medicines all night, so the
    /// refreshed packs carry on between midnight and then.
    /// </summary>
    public DowntimePackKind? Due(Guid facilityId, DateTimeOffset now, int refreshMinutes)
    {
        if (_lastFailed.TryGetValue(facilityId, out var failed) && now - failed < RetryAfter)
        {
            return null;
        }

        var local = NumberFormat.ToHospitalTime(now);
        if (local.Hour >= DowntimePackJob.RunAfterHour
            && (!_nightlyDone.TryGetValue(facilityId, out var day) || day != DateOnly.FromDateTime(local.DateTime)))
        {
            return DowntimePackKind.Nightly;
        }

        return refreshMinutes > 0 && (!_lastMade.TryGetValue(facilityId, out var made) || now - made >= TimeSpan.FromMinutes(refreshMinutes))
            ? DowntimePackKind.Refreshed
            : null;
    }

    /// <summary>A pack of <paramref name="kind"/> was made: the nightly one is done for the day.</summary>
    public void Made(Guid facilityId, DowntimePackKind kind, DateTimeOffset now)
    {
        _lastMade[facilityId] = now;
        _lastFailed.Remove(facilityId);
        if (kind == DowntimePackKind.Nightly)
        {
            _nightlyDone[facilityId] = DateOnly.FromDateTime(NumberFormat.ToHospitalTime(now).DateTime);
        }
    }

    /// <summary>The pack failed: the day is not marked done, so it is tried again after <see cref="RetryAfter"/>.</summary>
    public void Failed(Guid facilityId, DateTimeOffset now) => _lastFailed[facilityId] = now;
}

/// <summary>
/// Keeps each facility's pack current without anyone printing it: the nightly one after <see cref="RunAfterHour"/>
/// (hospital time), so a pack less than a day old is there when HC360 goes down without warning, retried through the day if
/// it failed; and at any hour a refreshed one every <c>downtime.pack.refreshMinutes</c> (30 if not set, 0 for nightly
/// only), because the ward sheets and medicines in it are as of the pack's time. Drops packs older than a week, and
/// refreshed ones after a few hours.
/// </summary>
internal sealed class DowntimePackJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<DowntimePackJob> logger) : BackgroundService
{
    internal const int RunAfterHour = 2;
    private readonly DowntimePackSchedule _schedule = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The downtime pack could not be kept current; will retry on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Makes what is due for each active facility now; returns how many packs were made.</summary>
    internal async Task<int> TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        int refreshMinutes;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            refreshMinutes = DowntimePackSchedule.RefreshMinutes(
                await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync<int?>(DowntimePackSchedule.RefreshMinutesSetting, null, ct));
        }

        var made = 0;
        foreach (var facilityId in await FacilitiesAsync(ct))
        {
            if (_schedule.Due(facilityId, now, refreshMinutes) is not { } kind)
            {
                continue;
            }

            if (await MakeAsync(facilityId, kind, ct))
            {
                _schedule.Made(facilityId, kind, now);
                made++;
            }
            else
            {
                // Not marked done: the nightly pack is tried again, not skipped until tomorrow.
                _schedule.Failed(facilityId, now);
            }
        }

        if (made > 0)
        {
            await PurgeAsync(ct);
        }

        return made;
    }

    /// <summary>One nightly pack for each active facility, now; one facility failing does not stop the others.</summary>
    internal async Task<int> RunAsync(CancellationToken ct)
    {
        var made = 0;
        foreach (var facilityId in await FacilitiesAsync(ct))
        {
            if (await MakeAsync(facilityId, DowntimePackKind.Nightly, ct))
            {
                made++;
            }
        }

        await PurgeAsync(ct);
        return made;
    }

    private async Task<List<Guid>> FacilitiesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Facility>().AsNoTracking().IgnoreQueryFilters()
            .Where(f => !f.IsDeleted).Select(f => f.Id).ToListAsync(ct);
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DowntimePackService>().PurgeAsync(ct);
    }

    private async Task<bool> MakeAsync(Guid facilityId, DowntimePackKind kind, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var packs = scope.ServiceProvider.GetRequiredService<DowntimePackService>();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var pack = await packs.BuildAsync(facilityId, kind, null, canSeeRestricted: false, ct);
            packs.Keep(pack, facilityId);
            var nightly = kind == DowntimePackKind.Nightly;
            scope.ServiceProvider.GetRequiredService<IAuditLogger>().Add(new AuditEntry(AuditCategories.PhiAccess,
                nightly ? "patients.downtime_pack_made" : "patients.downtime_pack_refreshed",
                $"Made the {(nightly ? "nightly" : "refreshed")} pre-downtime pack ({pack.Patients} in-patients)")
            {
                EntityType = nameof(DowntimePackSnapshot),
                EntityId = pack.Id.ToString(),
            });
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The {Kind} downtime pack of facility {FacilityId} failed; it will be tried again", kind, facilityId);
            return false;
        }
    }
}
