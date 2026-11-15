using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// The key tables whose rows a backup records in its manifest (<see cref="BackupManifest.RowCounts"/>), counted in the
/// very snapshot pg_dump dumps (<see cref="IPostgresTools.DumpAsync"/>), and a test restore counts again in the restored
/// database (<see cref="BackupRestoreTest"/>): the patients and their visits, appointments, encounters, notes,
/// prescriptions, lab results and admissions, the bills, receipts and pharmacy sales, the stored documents, the staff
/// accounts, the audit trail, and the schema's migrations. A table the database does not have is left out.
/// </summary>
public static partial class BackupRowCounts
{
    public static readonly IReadOnlyList<string> Tables =
    [
        "patients.patients",
        "patients.visits",
        "scheduling.appointments",
        "clinical.encounters",
        "clinical.clinical_notes",
        "clinical.prescriptions",
        "diagnostics.lab_results",
        "inpatient.admissions",
        "billing.bills",
        "billing.receipts",
        "pharmacy.sales",
        "platform.stored_files",
        "identity.users",
        "audit.audit_events",
        MigrationBaseline.HistorySchema + "." + MigrationBaseline.HistoryTable,
    ];

    /// <summary>
    /// The rows of each of <paramref name="tables"/> (<c>schema.table</c>) that the database of <paramref name="connection"/>
    /// has, in <paramref name="transaction"/>'s snapshot when one is given. However long the count takes: the audit trail
    /// of a few years is tens of millions of rows.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The table names are the fixed list above, each matched against an identifier pattern and quoted; nothing comes from a request.")]
    internal static async Task<IReadOnlyDictionary<string, long>> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<string> tables, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables.Where(t => TableName().IsMatch(t)))
        {
            await using (var exists = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection, transaction))
            {
                exists.Parameters.AddWithValue("table", table);
                if (await exists.ExecuteScalarAsync(ct) is not true)
                {
                    continue;
                }
            }

            var (schema, name) = (table[..table.IndexOf('.', StringComparison.Ordinal)], table[(table.IndexOf('.', StringComparison.Ordinal) + 1)..]);
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"{name}\"", connection, transaction) { CommandTimeout = 0 };
            counts[table] = (long)(await count.ExecuteScalarAsync(ct))!;
        }

        return counts;
    }

    /// <summary>Where the rows <paramref name="restored"/> differ from those <paramref name="recorded"/>; null when every table holds them.</summary>
    internal static string? Differences(IReadOnlyDictionary<string, long> recorded, IReadOnlyDictionary<string, long> restored)
    {
        var differences = recorded
            .Where(r => !restored.TryGetValue(r.Key, out var rows) || rows != r.Value)
            .Select(r => restored.TryGetValue(r.Key, out var rows)
                ? string.Create(CultureInfo.InvariantCulture, $"{r.Key} has {rows:N0} rows, and had {r.Value:N0} when the backup was made")
                : $"{r.Key} is missing")
            .ToList();
        return differences.Count == 0 ? null : $"The restored database is not the one backed up: {string.Join("; ", differences)}.";
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*\\.[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TableName();
}

/// <summary>A test restore's outcome, as recorded on the backup it tested.</summary>
internal sealed record BackupRestoreTestOutcome(Guid RunId, string FileName, bool Passed, string Note);

/// <summary>
/// Test restores (DED-16). A backup is only as good as its restore, and reading it back and listing its dump as it is made
/// (<see cref="BackupService"/>) restores nothing. So during the backups' nightly upkeep (<see cref="BackupUpkeepJob"/>),
/// once no test restore has passed for <see cref="BackupTestRestoreOptions.EveryDays"/>, the newest verified full backup
/// is opened as a restore opens it, its database restored by pg_restore, which stops at its first error, into a scratch
/// database on the hospital's PostgreSQL (<see cref="IScratchDatabase"/>), and its key tables' rows counted against those
/// its manifest recorded when it was made (<see cref="BackupRowCounts"/>); every document it lists must be in its backup
/// folder too, as a restore from that folder needs (<see cref="MissingDocuments"/>). The scratch database holds every patient's
/// record, so it is dropped afterwards whatever happened; one left by a test that was cut off (a power cut, a restart)
/// is dropped by the next. One test at a time for the hospital: a lock in the database is held for the whole test. The
/// outcome is recorded on the backup, which the Backups list shows; a failure is told to the administrators in the app,
/// once until a test passes again; and System health warns once none has passed for
/// <see cref="BackupTestRestoreOptions.WarnAfterDays"/> (<see cref="Networking.SystemHealthBackups"/>).
/// </summary>
internal sealed class BackupRestoreTest(
    IDbContextFactory<HospitalDbContext> contexts,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    RecoveryKeyStore recoveryKeys,
    IPostgresTools postgres,
    IScratchDatabase scratch,
    INotificationService notifications,
    TimeProvider time,
    ILogger<BackupRestoreTest> logger)
{
    /// <summary>The connection string of the account test restores sign in as: one that may create databases, and reads nothing of the hospital's.</summary>
    public const string ConnectionName = "BackupRestoreTest";

    /// <summary>The title of the in-app notification of a test restore that failed.</summary>
    public const string FailedTitle = "Backup test restore failed";

    /// <summary>The lock in the database held while a test restore runs, so the hospital's servers make one at a time.</summary>
    internal const long LockKey = 4_247_365;

    /// <summary>
    /// How much earlier than <see cref="BackupTestRestoreOptions.EveryDays"/> a test is due again: the upkeep runs once a
    /// night, so a test that passed at 03:45 is due again at 03:30 a week later, not eight nights on.
    /// </summary>
    private static readonly TimeSpan NightlyMargin = TimeSpan.FromHours(12);

    /// <summary>
    /// One test restore of the newest verified full backup the server can open, when one is due (or <paramref name="force"/>):
    /// its outcome, or null when none was due, there is no backup to test, or another server's test is under way.
    /// </summary>
    public async Task<BackupRestoreTestOutcome?> RunAsync(bool force, CancellationToken ct)
    {
        var settings = options.Value.Backup.TestRestore;
        if (!settings.Enabled && !force)
        {
            return null;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var lastPassed = await db.Set<BackupRun>().Where(r => r.RestoreTestPassed == true).MaxAsync(r => r.RestoreTestedAt, ct);
        if (!force && !Due(lastPassed, settings.EveryDays, time.GetUtcNow()))
        {
            return null;
        }

        // The newest backup this server can open: one made with its current recovery key (backups made before they named
        // their key open with the original one). Right after a new key, the newest backup was made with the key before.
        var keyId = recoveryKeys.CurrentKeyId();
        var original = recoveryKeys.IsOriginal;
        var run = await db.Set<BackupRun>()
            .Where(r => r.Status == BackupStatus.Succeeded && r.VerifiedAt != null && r.FileName != null && r.FormatVersion >= BackupEncryption.ArchiveVersion
                        && (r.Kind == BackupKind.Scheduled || r.Kind == BackupKind.Manual)
                        && (r.RecoveryKeyId == keyId || (original && r.RecoveryKeyId == null)))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (run is null)
        {
            return null;
        }

        var lastFailed = await db.Set<BackupRun>().Where(r => r.RestoreTestedAt != null)
            .OrderByDescending(r => r.RestoreTestedAt)
            .Select(r => r.RestoreTestPassed)
            .FirstOrDefaultAsync(ct) == false;

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(ct))
            {
                logger.LogInformation("Another test restore of a backup is under way; this one is left to the next night");
                return null;
            }

            try
            {
                var outcome = await TestAsync(run, token => db.SaveChangesAsync(token), ct);
                if (!outcome.Passed && !lastFailed)
                {
                    await TellFailedAsync(outcome);
                }

                return outcome;
            }
            finally
            {
                await db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock({LockKey}) AS \"Value\"").SingleAsync(CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Whether a test restore is due, the last that passed being at <paramref name="lastPassed"/>; always when none has.</summary>
    internal static bool Due(DateTimeOffset? lastPassed, int everyDays, DateTimeOffset now) =>
        lastPassed is not { } passed || now - passed >= TimeSpan.FromDays(Math.Max(everyDays, 1)) - NightlyMargin;

    /// <summary>
    /// Restores <paramref name="run"/> into a scratch database and checks it, as the class says; records the outcome on the
    /// run, saved with <paramref name="save"/>. The scratch database and the plain database dump are removed whatever happens.
    /// </summary>
    internal async Task<BackupRestoreTestOutcome> TestAsync(BackupRun run, Func<CancellationToken, Task> save, CancellationToken ct)
    {
        var started = time.GetUtcNow();
        var dump = Path.Combine(directories.Temp, $"restore-test-{Guid.NewGuid():N}.dump");
        string? database = null;
        bool passed;
        string note;
        try
        {
            Directory.CreateDirectory(directories.Temp);
            var file = Locate(run.FileName!)
                       ?? throw new ScratchDatabaseException($"{run.FileName} is no longer on this server's disk, nor on the second disk or NAS, so it could not be restored.");
            var contents = await ReadAsync(file, dump, ct);
            database = await scratch.CreateAsync((2 * await postgres.GetDatabaseSizeAsync(ct)) + (1L << 30), ct);
            logger.LogInformation("Test-restoring backup {File} into the scratch database {Database}", run.FileName, database);
            await scratch.RestoreAsync(database, dump, ct);
            TryDelete(dump);
            (passed, note) = MissingDocuments(file, contents) is { } missing ? (false, missing) : await CheckAsync(database, contents.Manifest, started, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (passed, note) = (false, ex switch
            {
                ScratchDatabaseException => ex.Message,
                CryptographicException or InvalidDataException or BackupKeyMismatchException => $"The backup could not be read back: {ex.Message}",
                _ => $"The test restore failed: {ex.Message}",
            });
        }
        finally
        {
            TryDelete(dump);
        }

        if (database is not null)
        {
            try
            {
                await scratch.DropAsync(database, CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or ArgumentException)
            {
                // The next test drops it, before it makes its own.
                logger.LogError(ex, "The scratch database {Database} of a test restore could not be dropped", database);
                note += $" The scratch database {database} could not be dropped ({ex.Message}); the next test restore drops it.";
            }
        }

        if (passed)
        {
            logger.LogInformation("Backup {File} passed its test restore: {Note}", run.FileName, note);
        }
        else
        {
            logger.LogError("Backup {File} failed its test restore: {Note}", run.FileName, note);
        }

        run.RestoreTestedAt = time.GetUtcNow();
        run.RestoreTestPassed = passed;
        run.RestoreTestNote = note.Length > 2000 ? note[..2000] : note;
        await save(CancellationToken.None);
        return new BackupRestoreTestOutcome(run.Id, run.FileName!, passed, note);
    }

    /// <summary>The restored database's key tables against the rows the backup recorded, and what the run's note says of it.</summary>
    private async Task<(bool Passed, string Note)> CheckAsync(string database, BackupManifest manifest, DateTimeOffset started, CancellationToken ct)
    {
        var minutes = Math.Round((time.GetUtcNow() - started).TotalMinutes);
        var took = minutes switch
        {
            < 1 => "under a minute",
            1 => "1 minute",
            _ => string.Create(CultureInfo.InvariantCulture, $"{minutes:0} minutes"),
        };
        if (manifest.RowCounts is not { Count: > 0 } recorded)
        {
            return (true, $"Restored without error into a scratch database, in {took}, and the scratch database dropped. The backup recorded no row counts to " +
                          "check them against: it was made before HC360 recorded them.");
        }

        var restored = await scratch.CountRowsAsync(database, [.. recorded.Keys], ct);
        return BackupRowCounts.Differences(recorded, restored) is { } differences
            ? (false, differences)
            : (true, string.Create(CultureInfo.InvariantCulture,
                $"Restored without error into a scratch database, in {took}, and the scratch database dropped: its {recorded.Count} key tables hold the " +
                $"{recorded.Values.Sum():N0} rows recorded when the backup was made."));
    }

    /// <summary>
    /// The documents a format 3 backup lists that its backup folder has no copy of, of the length its index gives: a restore
    /// from that folder would not put them back (<see cref="BackupBlobs"/>). Null when every one is there, or the backup holds
    /// its documents itself. Their content is read back by the upkeep on the second disk, and by <c>backup verify</c>.
    /// </summary>
    internal static string? MissingDocuments(string archive, BackupContents contents)
    {
        if (contents.Index.Count == 0)
        {
            return null;
        }

        var folder = BackupBlobs.FolderOf(archive);
        var missing = contents.Index.Where(d => !BackupBlobs.Candidates(folder, d.BlobId).Any(path => new FileInfo(path).Length == d.Length)).ToList();
        return missing.Count == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"The database restored, but {missing.Count} of the backup's {contents.Index.Count} documents have no copy in its backup folder ({folder}), so a restore from it would not put them back: the first is {missing[0].RelativePath}.");
    }

    /// <summary>Opens the backup as a restore opens it, checks it in full, and writes its database dump to <paramref name="dump"/>.</summary>
    private async Task<BackupContents> ReadAsync(string file, string dump, CancellationToken ct)
    {
        var key = recoveryKeys.Current();
        try
        {
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await using var payload = await BackupEncryption.OpenAsync(input, key.Key, ct);
            var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(DatabaseDumpPath: dump), ct);
            CryptographicOperations.ZeroMemory(contents.MasterKey);
            foreach (var ringKey in contents.RingKeys)
            {
                CryptographicOperations.ZeroMemory(ringKey);
            }

            if (contents.AuditSealKey is { } sealKey)
            {
                CryptographicOperations.ZeroMemory(sealKey);
            }

            if (contents.HospitalCa is { } ca)
            {
                CryptographicOperations.ZeroMemory(ca);
            }

            return contents;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key.Key);
        }
    }

    private async Task TellFailedAsync(BackupRestoreTestOutcome outcome)
    {
        try
        {
            await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, FailedTitle,
                $"{outcome.FileName} was restored into a scratch database to check it, and the test failed: {outcome.Note} The Backups screen has each " +
                "test's outcome, and the deployment guide (section 8, \"Test restores\") what to do.",
                "/admin/backups", "system", "warning", CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not tell the administrators that backup {File} failed its test restore", outcome.FileName);
        }
    }

    /// <summary>The backup file: on this server's disk, else on the second disk or NAS.</summary>
    private string? Locate(string fileName)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var directory in new[] { directories.Backups, options.Value.Backup.SecondaryDirectory })
        {
            if (directory is { Length: > 0 } && Path.Combine(directory, fileName) is var path && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unencrypted: the next backup removes a dump left in the temporary folder for over 12 hours (BackupService).
            logger.LogWarning(ex, "The database dump {File} of a test restore could not be removed", path);
        }
    }
}
