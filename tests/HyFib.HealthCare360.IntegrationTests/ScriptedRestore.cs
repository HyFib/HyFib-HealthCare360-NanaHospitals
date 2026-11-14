using System.ComponentModel;
using System.Diagnostics;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A database restored as Restore-HC360.ps1 restores one, step for step, for the tests that restore a backup:
/// <list type="number">
/// <item>a new database owned by an account that is not a superuser, as hc360_owner is on a Server Node (with the account
/// the server works as beside it);</item>
/// <item>pg_trgm made as that account (<c>SET ROLE &lt;owner&gt;; CREATE EXTENSION IF NOT EXISTS pg_trgm</c>);</item>
/// <item>pg_restore <c>--no-owner --role=&lt;owner&gt; --exit-on-error</c>, signed in as the superuser;</item>
/// <item>hc360-database-roles.sql, which gives the working account its rights again.</item>
/// </list>
/// The tests used to restore as the superuser with no <c>--role</c>, and passed while every restore on a Server Node
/// failed: the dump's <c>COMMENT ON EXTENSION pg_trgm</c> needs the extension's owner, and the script had made the
/// extension as the superuser.
/// </summary>
internal sealed class ScriptedRestore : IAsyncDisposable
{
    private readonly TestDatabase _database;

    private ScriptedRestore(TestDatabase database, string suffix)
    {
        _database = database;
        Owner = "hc360_owner_" + suffix;
        App = "hc360_app_" + suffix;
    }

    /// <summary>The restored database, signed in to as the superuser the tests read it with.</summary>
    public string ConnectionString => _database.ConnectionString;

    /// <summary>The account that owns the restored schema, as hc360_owner does.</summary>
    public string Owner { get; }

    /// <summary>The account the server works as, as hc360_app is.</summary>
    public string App { get; }

    /// <summary>Restores <paramref name="dump"/>, as written by the server CLI's <c>backup decrypt</c>, into a new database.</summary>
    public static async Task<ScriptedRestore> RunAsync(string dump, CancellationToken ct)
    {
        var restore = new ScriptedRestore(await TestDatabase.CreateAsync(ct), Guid.NewGuid().ToString("N")[..10]);
        try
        {
            await restore.RestoreAsync(dump, ct);
            return restore;
        }
        catch
        {
            await restore.DisposeAsync();
            throw;
        }
    }

    /// <summary>pg_restore from PGBIN (the CI runner's PostgreSQL) or the PATH.</summary>
    public static string PgRestore() =>
        Environment.GetEnvironmentVariable("PGBIN") is { Length: > 0 } bin ? Path.Combine(bin, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore") : "pg_restore";

    public static bool PgRestoreInstalled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(PgRestore(), "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>A script of deploy/windows, found above the test's folder.</summary>
    public static string DeployScriptPath(string file)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "deploy", "windows", file);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"deploy/windows/{file} was not found above the test's folder.");
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();

        // The accounts belong to the PostgreSQL server, not the database: dropped once the database holding their objects is.
        // A throwaway container goes with its accounts.
        try
        {
            var server = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres", Pooling = false };
            await using var connection = new NpgsqlConnection(server.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {App}; DROP ROLE IF EXISTS {Owner}", CancellationToken.None);
        }
        catch (NpgsqlException)
        {
        }
    }

    private async Task RestoreAsync(string dump, CancellationToken ct)
    {
        var name = new NpgsqlConnectionStringBuilder(ConnectionString).Database;
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(ct);

            // CREATE DATABASE ... OWNER hc360_owner: the test's new database is handed to an owner that is not a superuser.
            await ExecuteAsync(connection, $"CREATE ROLE {Owner} NOLOGIN; CREATE ROLE {App} NOLOGIN; ALTER DATABASE \"{name}\" OWNER TO {Owner}", ct);

            // As the script makes it, so that the owner owns the extension and may run the dump's COMMENT ON EXTENSION.
            await ExecuteAsync(connection, $"SET ROLE {Owner}; CREATE EXTENSION IF NOT EXISTS pg_trgm; RESET ROLE", ct);
        }

        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        var startInfo = new ProcessStartInfo(PgRestore()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["--no-owner", $"--role={Owner}", "--exit-on-error", "--no-password", $"--host={target.Host}", $"--port={target.Port}",
                     $"--username={target.Username}", $"--dbname={target.Database}", dump])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PGPASSWORD"] = target.Password ?? string.Empty;
        using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("pg_restore could not be started."))
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await output;
            process.ExitCode.ShouldBe(0, $"pg_restore --no-owner --role={Owner} --exit-on-error, as Restore-HC360.ps1 runs it: {await errors}");
        }

        // The accounts' rights again, as the script runs hc360-database-roles.sql where the installer made two accounts.
        var roles = await File.ReadAllTextAsync(DeployScriptPath("hc360-database-roles.sql"), ct);
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using (var names = new NpgsqlCommand("SELECT set_config('hc360.owner_role', @owner, false), set_config('hc360.app_role', @app, false)", connection))
            {
                names.Parameters.AddWithValue("owner", Owner);
                names.Parameters.AddWithValue("app", App);
                await names.ExecuteNonQueryAsync(ct);
            }

            await ExecuteAsync(connection, roles, ct);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
