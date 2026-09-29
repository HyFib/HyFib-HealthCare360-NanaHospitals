using System.ComponentModel;
using System.Diagnostics;
using HyFib.HealthCare360.Platform.Hosting;
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

    /// <summary>Dumps the hospital database in pg_dump's custom format.</summary>
    Task DumpAsync(string outputFile, CancellationToken ct);

    /// <summary>
    /// Lists the dump with pg_restore, which reads its whole table of contents and fails on a damaged archive. Returns
    /// false, without checking, when pg_restore is not installed.
    /// </summary>
    Task<bool> TryValidateDumpAsync(string dumpFile, CancellationToken ct);
}

internal sealed class PostgresTools(IConfiguration configuration, IOptions<PlatformOptions> options, ILogger<PostgresTools> logger) : IPostgresTools
{
    private NpgsqlConnectionStringBuilder Connection => new(configuration.GetConnectionString("Hospital"));

    public string DatabaseName => Connection.Database ?? string.Empty;

    public async Task<long> GetDatabaseSizeAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(Connection.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_database_size(current_database())", connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task DumpAsync(string outputFile, CancellationToken ct)
    {
        var connection = Connection;
        var (exitCode, stderr) = await RunAsync(
            ResolvePgDump(),
            [
                "--format=custom", "--compress=6", "--no-password",
                $"--file={outputFile}", $"--host={connection.Host}", $"--port={connection.Port}",
                $"--username={connection.Username}", $"--dbname={connection.Database}",
            ],
            connection.Password,
            ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"pg_dump exited with code {exitCode}: {stderr}");
        }
    }

    public async Task<bool> TryValidateDumpAsync(string dumpFile, CancellationToken ct)
    {
        int exitCode;
        string stderr;
        try
        {
            (exitCode, stderr) = await RunAsync(ResolvePgRestore(), ["--list", dumpFile], password: null, ct);
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "pg_restore was not found, so backups are checked without it; set HealthCare360:Backup:PgRestorePath");
            return false;
        }

        return exitCode == 0 ? true : throw new InvalidOperationException($"pg_restore cannot read the database dump (exit code {exitCode}): {stderr}");
    }

    private static async Task<(int ExitCode, string Stderr)> RunAsync(string executable, IReadOnlyList<string> arguments, string? password, CancellationToken ct)
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
