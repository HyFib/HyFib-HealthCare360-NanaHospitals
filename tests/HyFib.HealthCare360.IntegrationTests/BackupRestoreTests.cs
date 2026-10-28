using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Audit P4: a backup is worth what its restore gives back. Each backup is read back and its database dump listed by
/// pg_restore as it is made, but nothing restored one and opened the hospital's data. Here the test server's own backup is
/// restored as Restore-HC360.ps1 restores it, through the same server CLI commands in the same order: <c>backup decrypt</c>
/// writes the database dump, pg_restore fills a new database, and only then <c>backup restore</c> puts the documents and the
/// keys back.
/// </summary>
public sealed class BackupRestoreTests(HospitalServer server)
{
    private const string CredentialKey = "integrations.restoretest.secret";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The disaster the backups are for: the Server Node's disk is gone, and its backup is restored on new hardware. That
    /// machine has Data Protection keys of its own (its DPAPI differs) and nothing of the old keys folder, so the documents,
    /// the integrations' credentials and the audit trail's seals read there only through the keys the backup carries.
    /// </summary>
    [Fact]
    public async Task A_backup_restored_on_new_hardware_opens_there_with_its_patients_documents_credentials_and_audit_trail()
    {
        Assert.SkipUnless(PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");

        // On the Server Node: a scanned document and an integration's credential, both encrypted with the field key.
        var scan = ScannedDocument();
        var credential = "sms-api-key-" + Guid.NewGuid().ToString("N");
        Guid document;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            document = (await scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(new MemoryStream(scan), "scan.pdf", "document", null, null, Ct)).Value.Id;
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(CredentialKey, credential, cancellationToken: Ct);
        }

        var machine = NewMachine();
        string? backup = null;
        try
        {
            var chainEnd = await SealedAsync();
            (backup, var recoveryKey) = await BackupAsync();
            await using var restored = await RestoreAsync(backup, recoveryKey, machine);

            // The new machine's own Data Protection keys, which the CLI used there, as the server there will.
            var protection = HospitalDataProtection.CreateProvider(machine);

            (await PatientsAsync(restored.ConnectionString)).ShouldBe(await PatientsAsync(LiveConnectionString()), "every patient is there");

            await using var db = Context(restored.ConnectionString);
            var protector = new FieldProtector(machine, protection);
            var read = await new EncryptedFileStore(db, machine, protector, protector, new HttpCurrentUser(new HttpContextAccessor()), TimeProvider.System)
                .ReadAsync(document, Ct);
            read.Value.Content.ShouldBe(scan, "the document reads as the file store reads it");

            // As the settings service reads a secret: the protected JSON of the value.
            var saved = await db.Set<SystemSetting>().SingleAsync(s => s.Key == CredentialKey && s.FacilityId == null, Ct);
            var json = protector.Unprotect(JsonSerializer.Deserialize<string>(saved.Value)!).ShouldNotBeNull();
            JsonSerializer.Deserialize<string>(json).ShouldBe(credential);

            var check = await new AuditChainService(db, new AuditSealKey(machine, protection), new AuditAnchors(machine, NullLogger<AuditAnchors>.Instance),
                TimeProvider.System).VerifyAsync(Ct);
            check.Valid.ShouldBeTrue(check.Message);
            check.CheckedCount.ShouldBeGreaterThanOrEqualTo(chainEnd, "the audit trail sealed before the backup is back, its seals verifying with the backup's audit key");
        }
        finally
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(CredentialKey, null, CancellationToken.None);
            }

            if (backup is not null)
            {
                File.Delete(backup);
            }

            Directory.Delete(machine.Root, recursive: true);
        }
    }

    /// <summary>
    /// Waits until every audit record written so far is sealed, as the server seals them every few seconds (a test that runs
    /// first may back up before it has), and returns the position the chain ends at.
    /// </summary>
    private async Task<long> SealedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        for (var attempt = 0; ; attempt++)
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AuditChainService>().SealPendingAsync(Ct);
            var audit = scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking();
            if (!await audit.AnyAsync(e => e.Hash == null && e.OccurredAt <= now, Ct))
            {
                return await audit.MaxAsync(e => e.ChainIndex, Ct) ?? 0;
            }

            attempt.ShouldBeLessThan(300, "The audit records were not sealed.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }

    /// <summary>A backup as Administration › Backups › Back up now makes it, and the recovery key that opens it.</summary>
    private async Task<(string File, byte[] RecoveryKey)> BackupAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(BackupKind.Manual, null, Ct);
        run.Status.ShouldBe(BackupStatus.Succeeded, run.Error);
        run.Error.ShouldBeNull("pg_restore lists the backup's database as it is made");
        var key = server.Services.GetRequiredService<RecoveryKeyStore>().Current();
        key.KeyId.ShouldBe(run.RecoveryKeyId);
        return (Path.Combine(server.Services.GetRequiredService<DataDirectories>().Backups, run.FileName!), key.Key);
    }

    /// <summary>
    /// What Restore-HC360.ps1 does with a backup on <paramref name="machine"/>, step by step:
    /// <list type="number">
    /// <item>the server CLI's <c>backup decrypt</c> checks the whole backup and writes its database dump, and changes
    /// nothing else;</item>
    /// <item>a new database, with pg_trgm, filled by pg_restore;</item>
    /// <item>only then the CLI's <c>backup restore</c> puts back the documents and the backup's keys.</item>
    /// </list>
    /// </summary>
    private static async Task<TestDatabase> RestoreAsync(string backup, byte[] recoveryKey, DataDirectories machine)
    {
        var dump = Path.Combine(machine.Temp, "restore.dump");
        (await CliAsync(recoveryKey, "backup", "decrypt", "--in", backup, "--out", dump, "--data-dir", machine.Root)).ShouldBe(0, "backup decrypt");
        File.Exists(dump).ShouldBeTrue();
        File.Exists(FieldMasterKey.PathIn(machine)).ShouldBeFalse("backup decrypt puts no key in place");

        var database = await TestDatabase.CreateAsync(Ct);
        try
        {
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(Ct);
                await using var extension = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pg_trgm", connection);
                await extension.ExecuteNonQueryAsync(Ct);
            }

            await PgRestoreAsync(dump, database.ConnectionString);
            (await CliAsync(recoveryKey, "backup", "restore", "--in", backup, "--data-dir", machine.Root)).ShouldBe(0, "backup restore");
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The server CLI run as the script runs it: the recovery key in HC360_RECOVERY_KEY for the call only, never on the
    /// command line.
    /// </summary>
    private static async Task<int> CliAsync(byte[] recoveryKey, params string[] arguments)
    {
        Environment.SetEnvironmentVariable("HC360_RECOVERY_KEY", BackupRecoveryKey.Format(recoveryKey));
        try
        {
            return await ServerCli.RunAsync(arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HC360_RECOVERY_KEY", null);
        }
    }

    /// <summary>pg_restore as Restore-HC360.ps1 runs it, as the new database's owner here (the script restores as hc360_app).</summary>
    private static async Task PgRestoreAsync(string dump, string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var startInfo = new ProcessStartInfo(PgRestore()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["--no-owner", "--exit-on-error", "--no-password", $"--host={target.Host}", $"--port={target.Port}",
                     $"--username={target.Username}", $"--dbname={target.Database}", dump])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PGPASSWORD"] = target.Password ?? string.Empty;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("pg_restore could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        await output;
        process.ExitCode.ShouldBe(0, await errors);
    }

    /// <summary>pg_restore from PGBIN (the CI runner's PostgreSQL) or the PATH.</summary>
    private static string PgRestore() =>
        Environment.GetEnvironmentVariable("PGBIN") is { Length: > 0 } bin ? Path.Combine(bin, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore") : "pg_restore";

    private static bool PgRestoreInstalled()
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

    /// <summary>New hardware: a data folder of its own, with nothing of the Server Node's in it.</summary>
    private static DataDirectories NewMachine()
    {
        var machine = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "hc360-tests", "restored-" + Guid.NewGuid().ToString("N")) });
        machine.EnsureCreated();
        return machine;
    }

    /// <summary>A PDF as a scanner makes one, as far as the file store looks: its header, then pages of its own.</summary>
    private static byte[] ScannedDocument() => [.. "%PDF-1.7\n"u8, .. RandomNumberGenerator.GetBytes(4096)];

    private static async Task<long> PatientsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM patients.patients", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private string LiveConnectionString() => server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital")!;

    private HospitalDbContext Context(string connectionString) => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(ServerComposition).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable(MigrationBaseline.HistoryTable, MigrationBaseline.HistorySchema);
            })
            .UseSnakeCaseNamingConvention()
            .Options,
        server.Services.GetRequiredService<ModuleCatalog>());
}
