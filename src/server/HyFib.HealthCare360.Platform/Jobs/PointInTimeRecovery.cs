using System.Data.Common;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// The services and jobs of point-in-time recovery (SRS REL-02) and of the warm standby's safety (SRS DED-20), each
/// registered with one line in the platform's set-up.
/// </summary>
internal static class ReplicationRegistration
{
    public static IServiceCollection AddReplication(this IServiceCollection services)
    {
        services.AddScoped<BaseBackupService>();
        services.AddSingleton<IBaseBackupTool, PgBaseBackupTool>();
        services.AddScoped<WalArchiveService>();

        // On every server whatever its role: a failover may have replaced the database any of them runs on.
        services.AddHostedService<PrimaryFenceWatch>();
        return services;
    }

    /// <summary>
    /// Whether nightly backups are on or not, as the off-site copy's, the point-in-time recovery jobs, which do nothing until
    /// PostgreSQL archives the WAL (Enable-HC360Replication.ps1 -WalArchive); and, with nightly backups on, the backup after
    /// a failover or a key rotation.
    /// </summary>
    public static void ScheduleReplicationJobs(this IQuartzBuilder quartz, PlatformOptions options)
    {
        quartz.ScheduleJob<BaseBackupJob>(trigger => trigger
            .WithIdentity("base-backup")
            .WithCronSchedule(BaseBackupJob.CheckSchedule, cron => cron.InTimeZone(TimeZoneInfo.Utc)));
        quartz.ScheduleJob<WalArchiveJob>(trigger => trigger
            .WithIdentity("wal-archive")
            .WithCronSchedule(WalArchiveJob.Schedule, cron => cron.InTimeZone(TimeZoneInfo.Utc)));
        if (options.Backup.Enabled)
        {
            quartz.ScheduleJob<BackupAfterChangeJob>(trigger => trigger
                .WithIdentity("backup-after-change")
                .WithCronSchedule(BackupAfterChangeJob.Schedule, cron => cron.InTimeZone(TimeZoneInfo.Utc)));
        }
    }
}

/// <summary>
/// What the point-in-time recovery jobs did last (SRS REL-02), kept in <c>config\point-in-time-recovery.json</c> for System
/// health and for the jobs' own next run: when the last base backup was made, and when the last attempt failed and why.
/// </summary>
public sealed record PointInTimeRecoveryStatus
{
    /// <summary>When the newest base backup was made, and its file.</summary>
    public DateTimeOffset? BaseBackupAt { get; init; }

    public string? BaseBackupFile { get; init; }

    /// <summary>When the last base backup attempt failed, and why; null once one has succeeded since.</summary>
    public DateTimeOffset? BaseBackupFailedAt { get; init; }

    public string? BaseBackupError { get; init; }

    /// <summary>When a WAL file was last encrypted into the backup folder, and its name.</summary>
    public DateTimeOffset? WalSealedAt { get; init; }

    public string? WalSealed { get; init; }

    /// <summary>When encrypting the archived WAL last failed, and why; null once a run has encrypted all there was.</summary>
    public DateTimeOffset? WalFailedAt { get; init; }

    public string? WalError { get; init; }

    /// <summary>
    /// A WAL file PostgreSQL archived again with other content than the one already encrypted: it is left in the archive
    /// folder, unencrypted, for a person to look at, and nothing replaces the one kept.
    /// </summary>
    public string? WalConflict { get; init; }

    /// <summary>When everything encrypted was last found copied to the secondary directory.</summary>
    public DateTimeOffset? CopiedAt { get; init; }

    /// <summary>Since when the copies to the secondary directory have failed, and why the last one did; null while they succeed.</summary>
    public DateTimeOffset? CopyFailingSince { get; init; }

    public string? CopyError { get; init; }
}

/// <summary>PostgreSQL's WAL archiving as any account reads it: <c>archive_mode</c> and <c>archive_command</c>.</summary>
public sealed record ArchiveSettings(string Mode, string Command)
{
    /// <summary>Whether PostgreSQL archives each finished WAL file (Enable-HC360Replication.ps1 -WalArchive).</summary>
    public bool On => Mode != "off" && Command.Length > 0;
}

/// <summary>
/// Restoring the database to a moment between two nightly backups (SRS REL-02) takes a base backup, a copy of PostgreSQL's
/// whole data folder, and every WAL file archived since. Enable-HC360Replication.ps1 -WalArchive makes PostgreSQL archive
/// the WAL; HC360 takes the base backups (<see cref="BaseBackupService"/>), encrypted with the backup recovery key like the
/// nightly backups, in <c>base\&lt;system identifier&gt;</c> of the backup folder. They are kept by system identifier: a
/// cluster made afresh (new hardware) starts its WAL file names again, and must not be confused with the old one's.
/// </summary>
public static class PointInTimeRecovery
{
    public const string StatusFileName = "point-in-time-recovery.json";

    /// <summary>The folder of the backup folder (and of its second copy) the base backups are kept in.</summary>
    public const string BaseBackupFolderName = "base";

    /// <summary>The folder of the backup folder (and of its second copy) the encrypted WAL is kept in.</summary>
    public const string WalFolderName = "wal";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly Lock Gate = new();

    public static string StatusPath(DataDirectories directories) => Path.Combine(directories.Config, StatusFileName);

    /// <summary>Where the base backups of the cluster <paramref name="systemIdentifier"/> are kept under <paramref name="backups"/>.</summary>
    public static string BaseBackupFolder(string backups, string systemIdentifier) => Path.Combine(backups, BaseBackupFolderName, systemIdentifier);

    /// <summary>Where the encrypted WAL of the cluster <paramref name="systemIdentifier"/> is kept under <paramref name="backups"/>.</summary>
    public static string WalFolder(string backups, string systemIdentifier) => Path.Combine(backups, WalFolderName, systemIdentifier);

    /// <summary>The status as last saved; empty when there is none yet, or it cannot be read (it is written again).</summary>
    public static PointInTimeRecoveryStatus Load(DataDirectories directories)
    {
        try
        {
            return JsonSerializer.Deserialize<PointInTimeRecoveryStatus>(File.ReadAllBytes(StatusPath(directories)), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// Changes the saved status with <paramref name="change"/>, one job at a time: the base backup job and the WAL job each
    /// change only their own fields, and neither loses the other's.
    /// </summary>
    public static void Update(DataDirectories directories, Func<PointInTimeRecoveryStatus, PointInTimeRecoveryStatus> change)
    {
        lock (Gate)
        {
            Save(directories, change(Load(directories)));
        }
    }

    /// <summary>Saves the status under a temporary name first, so a reader never finds half a file.</summary>
    public static void Save(DataDirectories directories, PointInTimeRecoveryStatus status)
    {
        var path = StatusPath(directories);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(status, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads <c>archive_mode</c> and <c>archive_command</c> through <paramref name="connection"/>, which must be open.</summary>
    public static async Task<ArchiveSettings> ReadArchiveSettingsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_setting('archive_mode'), current_setting('archive_command')";
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ArchiveSettings(reader.GetString(0), reader.GetString(1))
            : new ArchiveSettings("off", string.Empty);
    }
}
