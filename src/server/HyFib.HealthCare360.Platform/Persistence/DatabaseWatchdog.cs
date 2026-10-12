using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>How PostgreSQL last left its data folder: DBState in <c>global\pg_control</c>.</summary>
public enum DatabaseClusterState
{
    /// <summary>pg_control could not be read, or does not look like one.</summary>
    Unknown = -1,
    Starting = 0,

    /// <summary>Shut down cleanly: someone, or a script, stopped PostgreSQL.</summary>
    ShutDown = 1,

    /// <summary>A standby that was shut down cleanly.</summary>
    ShutDownInRecovery = 2,
    ShuttingDown = 3,
    InCrashRecovery = 4,
    InArchiveRecovery = 5,

    /// <summary>Running, or stopped without shutting down: it crashed or was killed.</summary>
    InProduction = 6,
}

/// <summary>The PostgreSQL Windows service, as the service manager has it.</summary>
public enum DatabaseServiceState
{
    Running,
    Starting,
    Stopping,
    Stopped,
    Paused,

    /// <summary>No service of that name on this machine.</summary>
    Missing,
}

/// <summary>One start of PostgreSQL by the watchdog. <paramref name="Problem"/> says why it did not start.</summary>
public sealed record DatabaseRestart(DateTimeOffset At, bool Succeeded, string? Problem = null);

/// <summary>
/// What the watchdog last found (<c>config\database-watchdog.json</c>), with its most recent starts of PostgreSQL.
/// </summary>
public sealed record DatabaseWatchdogReport(
    DateTimeOffset CheckedAt,
    string Service,
    DatabaseServiceState ServiceState,
    DatabaseClusterState ClusterState,
    IReadOnlyList<DatabaseRestart> Restarts);

/// <summary>What the watchdog does on one check, and why, for its output.</summary>
public sealed record DatabaseWatchdogDecision(bool Start, string Reason);

/// <summary>
/// Starts PostgreSQL again when it stops without being asked to (SRS DED-01, REL-01). Windows restarts a service whose
/// process ends unexpectedly, and the installer sets that up, but PostgreSQL's service process (pg_ctl runservice) reports
/// a clean stop even when the database itself crashes or is killed, so Windows leaves it stopped. The "HC360 database
/// watchdog" scheduled task therefore runs <c>HyFib.HealthCare360.Server.exe database watchdog</c> as SYSTEM every minute:
/// when the service is set to start automatically but is stopped, and pg_control says the database was not shut down
/// cleanly, it starts the service. A clean shut-down is how a person or a script stops PostgreSQL, and is left alone, as
/// is a service set to start by hand. System health reads the record and says when PostgreSQL had to be started again.
/// </summary>
public static class DatabaseWatchdog
{
    public const string FileName = "database-watchdog.json";
    public const string ScheduledTaskName = "HC360 database watchdog";

    /// <summary>The task checks every minute; a record older than this means it has stopped.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>After this many starts in a row fail, the watchdog tries only every <see cref="RetryAfterFailures"/>.</summary>
    public const int FailuresBeforeBackoff = 3;

    public static readonly TimeSpan RetryAfterFailures = TimeSpan.FromMinutes(15);

    /// <summary>How long System health mentions a start, and how many starts the record keeps.</summary>
    public static readonly TimeSpan RecentRestarts = TimeSpan.FromHours(24);

    public const int RestartsKept = 20;

    private const int ControlHeaderLength = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathIn(DataDirectories directories) => Path.Combine(directories.Config, FileName);

    /// <summary>The state pg_control records in <paramref name="dataDirectory"/>; <see cref="DatabaseClusterState.Unknown"/> when it cannot be read.</summary>
    public static DatabaseClusterState ReadClusterState(string dataDirectory)
    {
        try
        {
            Span<byte> header = stackalloc byte[ControlHeaderLength];
            using var file = new FileStream(
                Path.Combine(dataDirectory, "global", "pg_control"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.ReadExactly(header);
            return ClusterStateFrom(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DatabaseClusterState.Unknown;
        }
    }

    /// <summary>
    /// The state in the first bytes of pg_control. Every PostgreSQL version since 9.x starts the file with the same fields,
    /// little-endian on Windows: system identifier (8 bytes), pg_control version (4), catalogue version (4), state (4).
    /// </summary>
    public static DatabaseClusterState ClusterStateFrom(ReadOnlySpan<byte> header)
    {
        if (header.Length < ControlHeaderLength)
        {
            return DatabaseClusterState.Unknown;
        }

        // PostgreSQL 13 to 16 write 1300, 17 writes 1700 and 18 writes 1800; anything else is not a pg_control HC360 knows.
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        var state = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
        return version is >= 1300 and < 10000 && state is >= 0 and <= 6 ? (DatabaseClusterState)state : DatabaseClusterState.Unknown;
    }

    /// <summary>
    /// The process id in <c>postmaster.pid</c>, which PostgreSQL writes as it starts and removes when it shuts down; null
    /// when there is none. After a crash the file stays, so the process may be gone, or be another one by now.
    /// </summary>
    public static int? ReadPostmasterId(string dataDirectory)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(
                Path.Combine(dataDirectory, "postmaster.pid"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return int.TryParse(reader.ReadLine(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether to start the service now. <paramref name="runsWithoutService"/> is true when PostgreSQL itself runs although
    /// its service is stopped: started by hand, or left running when the service process ended. <paramref name="restarts"/>
    /// are the watchdog's earlier starts, oldest first.
    /// </summary>
    public static DatabaseWatchdogDecision Decide(
        string service, DatabaseServiceState state, bool startsAutomatically, DatabaseClusterState cluster, bool runsWithoutService,
        IReadOnlyList<DatabaseRestart> restarts, DateTimeOffset now)
    {
        switch (state)
        {
            case DatabaseServiceState.Missing:
                return new(false, $"There is no service named {service} on this machine. Run Install-HC360Server.ps1 again to watch the PostgreSQL service HC360 uses.");
            case DatabaseServiceState.Running:
                return new(false, $"PostgreSQL ({service}) is running.");
            case DatabaseServiceState.Starting or DatabaseServiceState.Stopping or DatabaseServiceState.Paused:
                return new(false, $"PostgreSQL ({service}) is {state.ToString().ToLowerInvariant()}, so it is left as it is.");
        }

        if (!startsAutomatically)
        {
            return new(false, $"PostgreSQL ({service}) is stopped and set to start by hand, so it is left stopped.");
        }

        if (cluster is DatabaseClusterState.ShutDown or DatabaseClusterState.ShutDownInRecovery)
        {
            return new(false, $"PostgreSQL ({service}) was shut down cleanly, by a person or a script, so it is left stopped.");
        }

        // Its service would not start while it runs: PostgreSQL takes a data folder only once.
        if (runsWithoutService)
        {
            return new(false, $"PostgreSQL ({service}) is stopped, but PostgreSQL itself runs, started by hand or left running when its service process ended, so the service is not started.");
        }

        var failures = restarts.Reverse().TakeWhile(r => !r.Succeeded).ToList();
        if (failures.Count >= FailuresBeforeBackoff && now - failures[0].At < RetryAfterFailures)
        {
            var next = failures[0].At + RetryAfterFailures;
            return new(false, $"PostgreSQL ({service}) is stopped, and the last {failures.Count} starts failed: the next try is at {next.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC.");
        }

        var why = cluster == DatabaseClusterState.Unknown
            ? "its data folder's pg_control could not be read, so a clean shut-down cannot be told from a crash"
            : "it was not shut down cleanly";
        return new(true, $"PostgreSQL ({service}) is stopped, though it is set to start automatically, and {why}: starting it.");
    }

    /// <summary>The record's starts with <paramref name="restart"/> added, keeping the most recent <see cref="RestartsKept"/>.</summary>
    public static IReadOnlyList<DatabaseRestart> WithRestart(IReadOnlyList<DatabaseRestart> restarts, DatabaseRestart restart) =>
        [.. restarts.Append(restart).TakeLast(RestartsKept)];

    /// <summary>The last check's record, or null when there is none or it cannot be read.</summary>
    public static DatabaseWatchdogReport? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DatabaseWatchdogReport>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string path, DatabaseWatchdogReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(report, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// System health warnings about the watchdog's record. <paramref name="report"/> is null when it has never run, as on a
    /// Server Node whose PostgreSQL is on another machine: there is then nothing for it to watch.
    /// </summary>
    public static IReadOnlyList<HealthWarning> Warnings(DatabaseWatchdogReport? report, DateTimeOffset now)
    {
        if (report is null)
        {
            return [];
        }

        var warnings = new List<HealthWarning>();
        var recent = report.Restarts.Where(r => now - r.At < RecentRestarts).ToList();
        if (recent.Count > 0)
        {
            var times = recent.Count == 1 ? "once" : $"{recent.Count} times";

            // A start that took longer than the watchdog waited, through a long crash recovery, has worked once the service
            // runs.
            if (recent[^1] is { Succeeded: false } failed && report.ServiceState != DatabaseServiceState.Running)
            {
                warnings.Add(new("db.restart_failed", "critical",
                    $"PostgreSQL stopped without being shut down, and the database watchdog could not start it again: {failed.Problem} Its log folder (data\\log) says why it stopped."));
            }
            else
            {
                warnings.Add(new("db.restarted", "warning",
                    $"PostgreSQL stopped without being shut down {times} in the last 24 hours, and the database watchdog started it again. Its log folder (data\\log) says why it stopped."));
            }
        }

        if (report.ServiceState == DatabaseServiceState.Missing)
        {
            warnings.Add(new("db.watchdog_service_missing", "warning",
                $"The database watchdog watches the PostgreSQL service {report.Service}, which is no longer on this server. Run Install-HC360Server.ps1 again."));
        }

        if (now - report.CheckedAt > MaxAge)
        {
            warnings.Add(new("db.watchdog_stale", "warning",
                $"The database watchdog has not run since {report.CheckedAt.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)} UTC, so PostgreSQL is not started again if it stops. Check that the \"{ScheduledTaskName}\" scheduled task still runs."));
        }

        return warnings;
    }
}
