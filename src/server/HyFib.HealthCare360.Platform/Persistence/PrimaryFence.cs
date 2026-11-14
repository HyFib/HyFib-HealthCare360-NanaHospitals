using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// What a failover records (Invoke-HC360Failover.ps1, SRS DED-20): the PostgreSQL cluster <paramref name="SystemIdentifier"/>
/// has had its primary on <paramref name="Primary"/> since it was promoted to <paramref name="Timeline"/>. A standby is a
/// copy of the primary's cluster, so both share the system identifier, and promotion always starts a new timeline: a server
/// whose database is on an older timeline of the same cluster was replaced.
/// </summary>
/// <param name="Format">The record's format, 1.</param>
/// <param name="Primary">The machine that was promoted.</param>
/// <param name="PreviousPrimary">The address it streamed from until then, the server it replaced.</param>
public sealed record PrimaryRecord(int Format, string SystemIdentifier, int Timeline, string Primary, DateTimeOffset PromotedAt, string? PreviousPrimary = null);

/// <summary>
/// Keeps a replaced primary from taking patient data beside the standby promoted in its place (SRS DED-20). The failover
/// script writes its <see cref="PrimaryRecord"/> in <c>config\hc360-primary.json</c> on the promoted server and beside the
/// backups' second copy (<c>Backup:SecondaryDirectory</c>, the NAS or disk both servers are given), where the old primary
/// finds it when it comes back. A server whose database belongs to the same cluster on an older timeline was superseded:
/// HC360 does not start on it (<see cref="CheckAsync"/>), and one that runs stops within a minute of the record appearing
/// (<see cref="PrimaryFenceWatch"/>), until it is set up again as the new primary's standby, which gives it the new
/// timeline. Records of another cluster (another hospital's disk, new hardware restored from a dump) are no concern of it.
/// </summary>
public static class PrimaryFence
{
    public const string FileName = "hc360-primary.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The record on this server: the one its own failover wrote.</summary>
    public static string LocalPath(DataDirectories directories) => Path.Combine(directories.Config, FileName);

    /// <summary>The record beside the backups' second copy, which the servers share; null without a secondary directory.</summary>
    public static string? SharedPath(PlatformOptions options) =>
        options.Backup.SecondaryDirectory is { Length: > 0 } secondary ? Path.Combine(secondary, FileName) : null;

    /// <summary>
    /// The records this server can read now; one missing, unreachable (the disk unplugged) or unreadable is left out. Without
    /// <paramref name="directories"/> (a host that registers no data folders, such as a database start-up run on its own)
    /// there is no record of this server's own, and only the shared one is read.
    /// </summary>
    public static IReadOnlyList<PrimaryRecord> Read(DataDirectories? directories, PlatformOptions options) =>
        [.. new[] { directories is null ? null : LocalPath(directories), SharedPath(options) }.OfType<string>().Select(TryRead).OfType<PrimaryRecord>()];

    /// <summary>The record in <paramref name="path"/>; null when there is none, or it cannot be read as one.</summary>
    public static PrimaryRecord? TryRead(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize<PrimaryRecord>(File.ReadAllBytes(path), Json);
            return record is { Format: 1, SystemIdentifier.Length: > 0, Timeline: > 0, Primary.Length: > 0 } ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The newest record of <paramref name="own"/>'s cluster on a newer timeline than its own: it was replaced by that primary. Null when it was not.</summary>
    public static PrimaryRecord? SupersededBy(DatabaseCluster own, IEnumerable<PrimaryRecord> records) =>
        records.Where(r => r.SystemIdentifier == own.SystemIdentifier && r.Timeline > own.Timeline).MaxBy(r => r.Timeline);

    /// <summary>The record of <paramref name="machine"/>'s own promotion to <paramref name="own"/>'s timeline; null when it was not promoted to it.</summary>
    public static PrimaryRecord? PromotionOf(DatabaseCluster own, string machine, IEnumerable<PrimaryRecord> records) =>
        records.FirstOrDefault(r => r.SystemIdentifier == own.SystemIdentifier && r.Timeline == own.Timeline
            && string.Equals(r.Primary, machine, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why HC360 does not run on a database <paramref name="record"/> superseded.</summary>
    public static string SupersededMessage(PrimaryRecord record, DatabaseCluster own) =>
        $"This server's database was replaced by a promoted standby: {record.Primary} has been the primary since " +
        $"{record.PromotedAt.UtcDateTime.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)} UTC (timeline {record.Timeline}), and this database " +
        $"stayed on timeline {own.Timeline}. HC360 does not run on it, so that two servers never take patient data at once. Set this server up again as " +
        $"the standby of {record.Primary} with New-HC360Standby.ps1 -ReplaceExistingData, which keeps its data folder aside " +
        "(docs/deployment/DEPLOYMENT.md, \"Making the old primary the new standby\").";

    /// <summary>
    /// Stops start-up when the database was superseded (<see cref="SupersededMessage"/>), before anything is written.
    /// Without a record, as on every server that never failed over, PostgreSQL is not even asked. Without
    /// <paramref name="directories"/>, only the record beside the backups' second copy is read (<see cref="Read"/>).
    /// </summary>
    /// <exception cref="DatabaseUpgradeException">The database was superseded.</exception>
    public static async Task CheckAsync(HospitalDbContext db, PlatformOptions options, DataDirectories? directories, CancellationToken ct)
    {
        if (await SupersededAsync(db, options, directories, ct) is { } superseded)
        {
            throw new DatabaseUpgradeException(SupersededMessage(superseded.Record, superseded.Cluster));
        }
    }

    /// <summary>The record that superseded the database, with the database's cluster; null when none did.</summary>
    public static async Task<(PrimaryRecord Record, DatabaseCluster Cluster)?> SupersededAsync(
        HospitalDbContext db, PlatformOptions options, DataDirectories? directories, CancellationToken ct)
    {
        var records = Read(directories, options);
        if (records.Count == 0)
        {
            return null;
        }

        var cluster = await ReadClusterAsync(db, ct);
        return SupersededBy(cluster, records) is { } record ? (record, cluster) : null;
    }

    /// <summary>The database's cluster, through a connection of its own or the one <paramref name="db"/> holds open.</summary>
    public static async Task<DatabaseCluster> ReadClusterAsync(HospitalDbContext db, CancellationToken ct)
    {
        var wasOpen = db.Database.GetDbConnection().State == System.Data.ConnectionState.Open;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            return await DatabaseCluster.ReadAsync(db.Database.GetDbConnection(), ct);
        }
        finally
        {
            if (!wasOpen)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}

/// <summary>
/// Checks every minute, on every server whatever its role, whether a failover superseded the database
/// (<see cref="PrimaryFence"/>): a primary cut off from its standby by the network may run on while the standby is promoted,
/// and workstations that still reach it would write records the new primary never sees. On finding the record it stops the
/// server, which start-up then refuses to run again.
/// </summary>
[NodeLocal]
internal sealed class PrimaryFenceWatch(
    IServiceScopeFactory scopes,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<PrimaryFenceWatch> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (await SupersededAsync(stoppingToken) is { } message)
            {
                logger.LogCritical("HC360 stops: {Reason}", message);
                lifetime.StopApplication();
                return;
            }
        }
    }

    /// <summary>Why the server must stop now; null when it need not, or the database cannot be asked this minute.</summary>
    internal async Task<string?> SupersededAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            return await PrimaryFence.SupersededAsync(db, options.Value, directories, ct) is { } superseded
                ? PrimaryFence.SupersededMessage(superseded.Record, superseded.Cluster)
                : null;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
        {
            logger.LogDebug(ex, "Could not check whether a failover replaced this server's database; checking again in a minute");
            return null;
        }
    }
}
