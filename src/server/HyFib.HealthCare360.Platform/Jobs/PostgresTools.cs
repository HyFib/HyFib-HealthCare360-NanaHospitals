using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>The PostgreSQL client tools backups use: pg_dump to dump the hospital database and pg_restore to check a dump.</summary>
public interface IPostgresTools
{
    /// <summary>The hospital database's name (from <c>ConnectionStrings:Hospital</c>).</summary>
    string DatabaseName { get; }

    /// <summary>The database's size on disk (<c>pg_database_size</c>), to check a backup has room before it starts.</summary>
    Task<long> GetDatabaseSizeAsync(CancellationToken ct);

    /// <summary>
    /// Dumps the hospital database in pg_dump's custom format, without the rows of the servers that made it
    /// (<see cref="PostgresTools.ServerDataLeftOut"/>).
    /// </summary>
    /// <param name="lowPriority">Run pg_dump below normal priority, for a backup taken while the hospital works.</param>
    /// <returns>
    /// The rows of the key tables (<see cref="BackupRowCounts.Tables"/>) in the snapshot the dump holds, for a test restore
    /// to check; empty when they could not be counted, as the dump is made all the same.
    /// </returns>
    Task<IReadOnlyDictionary<string, long>> DumpAsync(string outputFile, bool lowPriority, CancellationToken ct);

    /// <summary>
    /// Lists the dump with pg_restore, which reads its whole table of contents and fails on a damaged archive. Returns
    /// false, without checking, when pg_restore is not installed.
    /// </summary>
    Task<bool> TryValidateDumpAsync(string dumpFile, CancellationToken ct);
}

/// <summary>
/// The scratch database a test restore fills (<see cref="BackupRestoreTest"/>), on the hospital's PostgreSQL, made and
/// dropped by the account of <c>ConnectionStrings:BackupRestoreTest</c>, or the hospital's own where that is not set.
/// </summary>
internal interface IScratchDatabase
{
    /// <summary>
    /// Drops what an earlier test restore left (cut off by a power cut or a restart), checks that PostgreSQL's disk has
    /// <paramref name="roomNeeded"/> bytes free, and makes an empty database; returns its name.
    /// </summary>
    /// <exception cref="ScratchDatabaseException">It cannot be made: why, and what to do, in words for the administrator.</exception>
    Task<string> CreateAsync(long roomNeeded, CancellationToken ct);

    /// <summary>Restores <paramref name="dumpFile"/> into <paramref name="database"/> with pg_restore, which stops at its first error.</summary>
    /// <exception cref="ScratchDatabaseException">pg_restore stopped at an error, which it says.</exception>
    Task RestoreAsync(string database, string dumpFile, CancellationToken ct);

    /// <summary>The rows of <paramref name="tables"/> in <paramref name="database"/>; a table it does not have is left out.</summary>
    Task<IReadOnlyDictionary<string, long>> CountRowsAsync(string database, IReadOnlyCollection<string> tables, CancellationToken ct);

    /// <summary>Drops <paramref name="database"/>, which must be one <see cref="CreateAsync"/> made, whoever is still connected to it.</summary>
    Task DropAsync(string database, CancellationToken ct);
}

/// <summary>Why a scratch database cannot be made or filled, in words for the administrator.</summary>
internal sealed class ScratchDatabaseException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed partial class PostgresTools(
    IConfiguration configuration, DatabaseCredentials credentials, IOptions<PlatformOptions> options, ILogger<PostgresTools> logger) : IPostgresTools, IScratchDatabase
{
    /// <summary>The names of the scratch databases a test restore makes; only these are ever dropped.</summary>
    internal const string ScratchPrefix = "hc360_restore_test_";
    /// <summary>
    /// The table whose rows a backup leaves out, its schema kept: each server's part of System health
    /// (<see cref="Networking.ServerNodeBoard"/>), the heartbeat of the machines that made the backup, not hospital data.
    /// Restored on new hardware, those rows showed the old machines as servers that stopped reporting, told to IT every
    /// day until someone removed each; restored on the same machines, they are written again within a minute of starting.
    /// </summary>
    internal const string ServerDataLeftOut = "platform.server_nodes";

    // With the current password, which Administration may have changed since the server started.
    private NpgsqlConnectionStringBuilder Connection => new(credentials.WithCurrentPassword(configuration.GetConnectionString("Hospital")!));

    public string DatabaseName => Connection.Database ?? string.Empty;

    public async Task<long> GetDatabaseSizeAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(Connection.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_database_size(current_database())", connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyDictionary<string, long>> DumpAsync(string outputFile, bool lowPriority, CancellationToken ct)
    {
        var connection = Connection;

        // The key tables' rows are counted in a snapshot that pg_dump then dumps (--snapshot), exported by a transaction
        // held open until pg_dump is done: rows saved meanwhile are in neither, so a test restore of the dump finds exactly
        // these. A PostgreSQL that cannot export one, or count, leaves the dump uncounted, and made all the same.
        await using var session = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Pooling = false }.ConnectionString);
        NpgsqlTransaction? snapshotHolder = null;
        string? snapshot = null;
        IReadOnlyDictionary<string, long> counts = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            await session.OpenAsync(ct);
            snapshotHolder = await session.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await using (var export = new NpgsqlCommand("SELECT pg_export_snapshot()", session, snapshotHolder))
            {
                snapshot = (string?)await export.ExecuteScalarAsync(ct);
            }

            counts = await BackupRowCounts.CountAsync(session, snapshotHolder, BackupRowCounts.Tables, ct);
        }
        catch (NpgsqlException ex)
        {
            logger.LogWarning(ex, "The key tables' rows could not be counted, so this backup is made without the counts a test restore checks");
            (snapshot, counts) = (null, new Dictionary<string, long>(StringComparer.Ordinal));
        }

        try
        {
            List<string> arguments =
            [
                "--format=custom", "--compress=6", "--no-password", $"--exclude-table-data={ServerDataLeftOut}",
                $"--file={outputFile}", $"--host={connection.Host}", $"--port={connection.Port}",
                $"--username={connection.Username}", $"--dbname={connection.Database}",
            ];
            if (snapshot is not null)
            {
                arguments.Add($"--snapshot={snapshot}");
            }

            var (exitCode, stderr) = await RunAsync(ResolvePgDump(), arguments, connection.Password, lowPriority, ct);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"pg_dump exited with code {exitCode}: {stderr}");
            }

            return counts;
        }
        finally
        {
            if (snapshotHolder is not null)
            {
                await snapshotHolder.DisposeAsync();
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------- test restores

    /// <summary>
    /// The account test restores sign in as: <c>ConnectionStrings:BackupRestoreTest</c>, one that may create databases and
    /// reads nothing of the hospital's (DEPLOYMENT.md section 8), or else the hospital's own, as on a development machine.
    /// </summary>
    private NpgsqlConnectionStringBuilder RestoreConnection(string? database)
    {
        var builder = configuration.GetConnectionString(BackupRestoreTest.ConnectionName) is { Length: > 0 } own ? new NpgsqlConnectionStringBuilder(own) : Connection;
        builder.Pooling = false;
        if (database is not null)
        {
            builder.Database = database;
        }

        return builder;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The database's name is HC360's own (a fixed prefix and random hex); its encoding and locale are the hospital database's, from pg_database, quoted as literals.")]
    public async Task<string> CreateAsync(long roomNeeded, CancellationToken ct)
    {
        var account = RestoreConnection(null);
        await using var connection = new NpgsqlConnection(account.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
        }
        catch (NpgsqlException ex)
        {
            throw new ScratchDatabaseException($"The account test restores sign in as ({account.Username}) could not sign in to PostgreSQL: {ex.Message}", ex);
        }

        // What an earlier test restore left: a power cut, or a restart, in the middle of one. Only this account's own
        // scratch databases, by their names, never the hospital's.
        var leftovers = new List<string>();
        await using (var find = new NpgsqlCommand(
                         "SELECT datname FROM pg_database WHERE starts_with(datname, @prefix) AND datdba = (SELECT oid FROM pg_roles WHERE rolname = current_user)",
                         connection))
        {
            find.Parameters.AddWithValue("prefix", ScratchPrefix);
            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                leftovers.Add(reader.GetString(0));
            }
        }

        foreach (var leftover in leftovers.Where(IsScratch))
        {
            logger.LogWarning("Dropping the scratch database {Database} an earlier test restore left", leftover);
            await DropAsync(connection, leftover, ct);
        }

        await using (var may = new NpgsqlCommand("SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user", connection))
        {
            if (await may.ExecuteScalarAsync(ct) is not true)
            {
                throw new ScratchDatabaseException(
                    $"The account test restores sign in as, {account.Username}, may not create databases, so there is nowhere to restore to. Give HC360 one " +
                    $"that may (ConnectionStrings:{BackupRestoreTest.ConnectionName}), as the deployment guide says (section 8, \"Test restores\").");
            }
        }

        if (options.Value.Backup.TestRestore.CheckRoom)
        {
            await using var where = new NpgsqlCommand("SELECT setting FROM pg_settings WHERE name = 'data_directory'", connection);
            if (RoomProblem(await where.ExecuteScalarAsync(ct) as string, roomNeeded, FreeBytes) is { } problem)
            {
                throw new ScratchDatabaseException(problem);
            }
        }

        // As the hospital's database is made, so the restore reads its text as the hospital's does.
        string encoding = "UTF8", collate = string.Empty, ctype = string.Empty;
        await using (var like = new NpgsqlCommand("SELECT pg_encoding_to_char(encoding), datcollate, datctype FROM pg_database WHERE datname = @name", connection))
        {
            like.Parameters.AddWithValue("name", DatabaseName);
            await using var reader = await like.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                (encoding, collate, ctype) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            }
        }

        var name = ScratchPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var locale = collate.Length > 0 ? $" LC_COLLATE {Literal(collate)} LC_CTYPE {Literal(ctype)}" : string.Empty;
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0 ENCODING {Literal(encoding)}{locale}", connection))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        return name;
    }

    public async Task RestoreAsync(string database, string dumpFile, CancellationToken ct)
    {
        var target = RestoreConnection(database);

        // Every object owned by the account that restores, and none of the hospital's grants, which name its two accounts:
        // the scratch database is read by this account alone, then dropped.
        var (exitCode, stderr) = await RunAsync(
            ResolvePgRestore(),
            [
                "--exit-on-error", "--no-owner", "--no-privileges", "--no-tablespaces", "--no-publications", "--no-subscriptions", "--no-security-labels",
                "--no-password", $"--host={target.Host}", $"--port={target.Port}", $"--username={target.Username}", $"--dbname={database}", dumpFile,
            ],
            target.Password,
            lowPriority: true,
            ct);
        if (exitCode != 0)
        {
            throw new ScratchDatabaseException($"pg_restore stopped at an error (exit code {exitCode}): {(stderr.Length > 1000 ? stderr[..1000] + "…" : stderr)}");
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> CountRowsAsync(string database, IReadOnlyCollection<string> tables, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RestoreConnection(database).ConnectionString);
        await connection.OpenAsync(ct);
        return await BackupRowCounts.CountAsync(connection, null, tables, ct);
    }

    public async Task DropAsync(string database, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RestoreConnection(null).ConnectionString);
        await connection.OpenAsync(ct);
        await DropAsync(connection, database, ct);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The name is matched against the scratch databases' own pattern first, which allows letters, digits and underscores only.")]
    private async Task DropAsync(NpgsqlConnection connection, string database, CancellationToken ct)
    {
        if (!IsScratch(database))
        {
            throw new ArgumentException($"{database} is not a scratch database of a test restore.", nameof(database));
        }

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Whether <paramref name="database"/> is named as the scratch databases are, and is not the hospital's.</summary>
    internal bool IsScratch(string database) =>
        ScratchName().IsMatch(database) && !string.Equals(database, DatabaseName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why a restore needing <paramref name="needed"/> bytes should not start, with PostgreSQL's data in
    /// <paramref name="dataDirectory"/>: a disk that fills up stops the hospital's own database. Null when there is room.
    /// </summary>
    internal static string? RoomProblem(string? dataDirectory, long needed, Func<string, long?> freeBytes)
    {
        var neededMb = (needed / (1 << 20)).ToString("N0", CultureInfo.InvariantCulture);
        if (dataDirectory is null)
        {
            return $"HC360 cannot see where PostgreSQL keeps its data, so it cannot check there is room for the restored copy (about {neededMb} MB). Give the " +
                   "account test restores sign in as the pg_read_all_settings role, as the deployment guide says (section 8, \"Test restores\"); where the " +
                   "database is on another server, check its disk has the room and set HealthCare360:Backup:TestRestore:CheckRoom to false.";
        }

        if (freeBytes(dataDirectory) is not { } free)
        {
            return $"PostgreSQL keeps its data in {dataDirectory}, which is not on this server, so HC360 cannot check there is room for the restored copy " +
                   $"(about {neededMb} MB). Check that disk has the room, then set HealthCare360:Backup:TestRestore:CheckRoom to false.";
        }

        return free >= needed
            ? null
            : $"Not enough free space for the restored copy beside the hospital's database in {dataDirectory}: {(free / (1 << 20)).ToString("N0", CultureInfo.InvariantCulture)} MB " +
              $"free, about {neededMb} MB needed. A full disk would stop the hospital's own database, so nothing was restored: free up space on that disk.";
    }

    /// <summary>The free space of the disk <paramref name="path"/> is on; null when it is not a folder on this server.</summary>
    private static long? FreeBytes(string path)
    {
        try
        {
            return Directory.Exists(path) ? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^hc360_restore_test_[0-9a-f]{12}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScratchName();

    public async Task<bool> TryValidateDumpAsync(string dumpFile, CancellationToken ct)
    {
        int exitCode;
        string stderr;
        try
        {
            (exitCode, stderr) = await RunAsync(ResolvePgRestore(), ["--list", dumpFile], password: null, lowPriority: false, ct);
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "pg_restore was not found, so backups are checked without it; set HealthCare360:Backup:PgRestorePath");
            return false;
        }

        return exitCode == 0 ? true : throw new InvalidOperationException($"pg_restore cannot read the database dump (exit code {exitCode}): {stderr}");
    }

    private static async Task<(int ExitCode, string Stderr)> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? password, bool lowPriority, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The password reaches the tool through its environment, never the command line (visible to other processes).
        startInfo.Environment["PGPASSWORD"] = password ?? string.Empty;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.");
        if (lowPriority)
        {
            LowerPriority(process);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromHours(2));

        // Both pipes are drained while the tool runs (pg_restore --list writes a line per database object).
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await stdout;
            return (process.ExitCode, (await stderr).Trim());
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>
    /// Below normal priority, so the server's requests are served first while the tool runs: the CPU's share only, as
    /// Windows gives no way to lower another process's disk priority. A tool that has already ended, or a system that
    /// refuses, leaves it as it is.
    /// </summary>
    private static void LowerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Normal priority then: the backup is still made.
        }
    }

    private string ResolvePgDump()
    {
        var configured = options.Value.Backup.PgDumpPath;
        if (Path.IsPathRooted(configured) && File.Exists(configured))
        {
            return configured;
        }

        return FindInPostgresInstall("pg_dump.exe") ?? configured;
    }

    private string ResolvePgRestore()
    {
        if (options.Value.Backup.PgRestorePath is { Length: > 0 } configured)
        {
            return configured;
        }

        // The one beside pg_dump, so both come from the same PostgreSQL version.
        var pgDump = ResolvePgDump();
        if (Path.IsPathRooted(pgDump))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(pgDump)!, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return FindInPostgresInstall("pg_restore.exe") ?? "pg_restore";
    }

    private static string? FindInPostgresInstall(string tool)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // The newest installed version: folders are named by version ("18", "9.6"), so they are compared as versions,
        // not as text (as text, "9.6" would come before "18").
        var postgres = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        return Directory.Exists(postgres)
            ? Directory.GetDirectories(postgres)
                .Select(d => (Version: InstallVersion(Path.GetFileName(d)), Tool: Path.Combine(d, "bin", tool)))
                .Where(t => File.Exists(t.Tool))
                .OrderByDescending(t => t.Version)
                .Select(t => t.Tool)
                .FirstOrDefault()
            : null;
    }

    internal static Version InstallVersion(string folder) =>
        Version.TryParse(folder.Contains('.', StringComparison.Ordinal) ? folder : folder + ".0", out var version) ? version : new Version(0, 0);
}
