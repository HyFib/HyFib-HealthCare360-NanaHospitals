using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Numbering;
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
        var before = time.GetUtcNow() - DowntimePackSnapshot.KeptFor;
        await db.Set<DowntimePackSnapshot>().IgnoreQueryFilters().Where(s => s.GeneratedAt < before).ExecuteDeleteAsync(ct);
    }

    private static string Local(DateTimeOffset at) => NumberFormat.ToHospitalTime(at).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// Makes each facility's pack every night (after <see cref="RunAfterHour"/>, hospital time), so a pack less than a day
/// old is there when HC360 goes down without warning; drops packs older than a week.
/// </summary>
internal sealed class DowntimePackJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<DowntimePackJob> logger) : BackgroundService
{
    internal const int RunAfterHour = 2;
    private DateOnly? _lastRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30), time);
        do
        {
            try
            {
                var now = NumberFormat.ToHospitalTime(time.GetUtcNow());
                var today = DateOnly.FromDateTime(now.DateTime);
                if (now.Hour >= RunAfterHour && _lastRun != today)
                {
                    await RunAsync(stoppingToken);
                    _lastRun = today;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The nightly downtime pack failed; will retry on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pack for each active facility; one facility failing does not stop the others.</summary>
    internal async Task<int> RunAsync(CancellationToken ct)
    {
        List<Guid> facilities;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            facilities = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Facility>().AsNoTracking().IgnoreQueryFilters()
                .Where(f => !f.IsDeleted).Select(f => f.Id).ToListAsync(ct);
        }

        var made = 0;
        foreach (var facilityId in facilities)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var packs = scope.ServiceProvider.GetRequiredService<DowntimePackService>();
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                var pack = await packs.BuildAsync(facilityId, DowntimePackKind.Nightly, null, canSeeRestricted: false, ct);
                packs.Keep(pack, facilityId);
                scope.ServiceProvider.GetRequiredService<IAuditLogger>().Add(new AuditEntry(AuditCategories.PhiAccess, "patients.downtime_pack_made",
                    $"Made the nightly pre-downtime pack ({pack.Patients} in-patients)")
                {
                    EntityType = nameof(DowntimePackSnapshot),
                    EntityId = pack.Id.ToString(),
                });
                await db.SaveChangesAsync(ct);
                made++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The nightly downtime pack of facility {FacilityId} failed", facilityId);
            }
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DowntimePackService>().PurgeAsync(ct);
        }

        return made;
    }
}
