using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Networking;
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
        Assert.SkipUnless(ScriptedRestore.PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");

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
            var read = await new EncryptedFileStore(db, machine, protector, protector, new HttpCurrentUser(new HttpContextAccessor()), TimeProvider.System,
                    NullLogger<EncryptedFileStore>.Instance)
                .ReadAsync(document, Ct);
            read.Value.Content.ShouldBe(scan, "the document reads as the file store reads it");

            // As the settings service reads a secret: the protected JSON of the value.
            var saved = await db.Set<SystemSetting>().SingleAsync(s => s.Key == CredentialKey && s.FacilityId == null, Ct);
            var json = protector.Unprotect(JsonSerializer.Deserialize<string>(saved.Value)!).ShouldNotBeNull();
            JsonSerializer.Deserialize<string>(json).ShouldBe(credential);

            // The anchor file is kept outside the database and its backups (best on storage the database's administrators
            // cannot write): it is put back beside the restored server, which checks a purge of old months against it.
            var anchors = server.Services.GetRequiredService<AuditAnchors>().Path;
            if (File.Exists(anchors))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(machine.AuditAnchors)!);
                File.Copy(anchors, machine.AuditAnchors, overwrite: true);
            }

            var check = await new AuditChainService(db, new AuditSealKey(machine, protection), new AuditAnchors(machine, NullLogger<AuditAnchors>.Instance),
                TimeProvider.System).VerifyAsync(Ct);
            check.Valid.ShouldBeTrue(check.Message);
            check.CheckedCount.ShouldBeGreaterThanOrEqualTo(chainEnd - (check.PurgedThrough ?? 0),
                "the audit trail sealed before the backup is back (from the start of the chain, or the last purge), its seals verifying with the backup's audit key");
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
    /// The servers the backup was made with are not those of the new hardware: a backup holds none of their rows (System
    /// health's part of each server). Restored, those showed every old machine as a server that stopped reporting, told to
    /// IT every day until someone removed each; each server writes its own as it starts. A backup made before they were
    /// left out holds them: Restore-HC360.ps1 clears them after pg_restore, with its own statement, run here, and names them.
    /// </summary>
    [Fact]
    public async Task A_backup_restored_on_new_hardware_holds_none_of_the_old_servers()
    {
        Assert.SkipUnless(ScriptedRestore.PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");
        var old = new ServerNodeBoard(server.Services.GetRequiredService<IServiceScopeFactory>(), SystemHealthServersTests.NodeFolder());
        var name = "IT-OLD-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var machine = NewMachine();
        string? backup = null;
        try
        {
            await old.PublishAsync(SystemHealthServersTests.JobNode(name), Ct);
            (backup, var recoveryKey) = await BackupAsync();
            await using var restored = await RestoreAsync(backup, recoveryKey, machine);
            await using var connection = new NpgsqlConnection(restored.ConnectionString);
            await connection.OpenAsync(Ct);
            (await ServerRowsAsync(connection)).ShouldBe(0, "a backup holds no server's row: the old servers' showed as servers that stopped reporting");

            // A backup made before holds them: the script clears them, between pg_restore and putting the keys back.
            var script = await File.ReadAllTextAsync(ScriptedRestore.DeployScriptPath("Restore-HC360.ps1"), Ct);
            var restoredAt = script.IndexOf("pg_restore.exe') @pgArgs", StringComparison.Ordinal);
            var clearedAt = script.IndexOf("Clear-ServerRows $DatabaseName", StringComparison.Ordinal);
            var keysAt = script.IndexOf("@('backup', 'restore'", StringComparison.Ordinal);
            (restoredAt, clearedAt, keysAt).ShouldSatisfyAllConditions(
                o => o.restoredAt.ShouldBeGreaterThan(0),
                o => o.clearedAt.ShouldBeGreaterThan(o.restoredAt, "the rows are cleared once pg_restore has put them in"),
                o => o.keysAt.ShouldBeGreaterThan(o.clearedAt, "and before the keys, so a failure later puts the previous database back"));
            var clear = Regex.Match(script, "\\$ClearServerRows = \"(?<sql>[^\"]+)\"").Groups["sql"].Value;
            clear.ShouldNotBeNullOrEmpty("the script's statement that clears them");

            await using (var held = new NpgsqlCommand(
                "INSERT INTO platform.server_nodes (node, report, reported_at) VALUES ('old', CAST(@report AS jsonb), now() - interval '2 days')", connection))
            {
                held.Parameters.AddWithValue("report", JsonSerializer.Serialize(new { server = new { machineName = name } }));
                await held.ExecuteNonQueryAsync(Ct);
            }

            var cleared = new List<string>();
            await using (var command = new NpgsqlCommand(clear, connection))
            await using (var reader = await command.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    cleared.Add(reader.GetString(0));
                }
            }

            cleared.ShouldBe([name], "the script names the servers it clears");
            (await ServerRowsAsync(connection)).ShouldBe(0);
        }
        finally
        {
            await old.WithdrawAsync(CancellationToken.None);
            if (backup is not null)
            {
                File.Delete(backup);
            }

            Directory.Delete(machine.Root, recursive: true);
        }
    }

    /// <summary>
    /// A document damaged on the disk of the machine restored onto (a failing disk, ransomware) is put back by the restore,
    /// as Restore-HC360.ps1 runs <c>backup restore</c>: the archive records the hash of every document, and one already
    /// there that does not match it is replaced by the backup's copy, the damaged file kept beside the documents.
    /// </summary>
    [Fact]
    public async Task A_restore_puts_back_a_document_damaged_on_disk_and_keeps_the_damaged_file()
    {
        Assert.SkipUnless(ScriptedRestore.PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");
        StoredFile document;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            document = (await scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(new MemoryStream(ScannedDocument()), "scan.pdf", "document", null, null, Ct)).Value;
        }

        var machine = NewMachine();
        string? backup = null;
        try
        {
            (backup, var recoveryKey) = await BackupAsync();
            var original = await File.ReadAllBytesAsync(Path.Combine(server.Services.GetRequiredService<DataDirectories>().Files, document.StoragePath), Ct);
            var damaged = original.ToArray();
            damaged[^1] ^= 0xFF;
            var onDisk = Path.Combine(machine.Files, document.StoragePath);
            Directory.CreateDirectory(Path.GetDirectoryName(onDisk)!);
            await File.WriteAllBytesAsync(onDisk, damaged, Ct);

            (await CliAsync(recoveryKey, "backup", "restore", "--in", backup, "--data-dir", machine.Root)).ShouldBe(0, "backup restore");

            (await File.ReadAllBytesAsync(onDisk, Ct)).ShouldBe(original, "the backup's copy is put in place of the damaged file");
            var kept = Directory.GetDirectories(machine.Root, "files-replaced-*").ShouldHaveSingleItem();
            (await File.ReadAllBytesAsync(Path.Combine(kept, document.StoragePath), Ct)).ShouldBe(damaged, "the damaged file is kept, never deleted");
        }
        finally
        {
            if (backup is not null)
            {
                File.Delete(backup);
            }

            Directory.Delete(machine.Root, recursive: true);
        }
    }

    private static async Task<long> ServerRowsAsync(NpgsqlConnection connection)
    {
        await using var count = new NpgsqlCommand("SELECT count(*) FROM platform.server_nodes", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
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
    /// <item>a new database owned by an account that is not a superuser, with pg_trgm made as that account, filled by
    /// pg_restore as that account, and the working account's rights given again (<see cref="ScriptedRestore"/>);</item>
    /// <item>only then the CLI's <c>backup restore</c> puts back the documents and the backup's keys.</item>
    /// </list>
    /// </summary>
    private static async Task<ScriptedRestore> RestoreAsync(string backup, byte[] recoveryKey, DataDirectories machine)
    {
        var dump = Path.Combine(machine.Temp, "restore.dump");
        (await CliAsync(recoveryKey, "backup", "decrypt", "--in", backup, "--out", dump, "--data-dir", machine.Root)).ShouldBe(0, "backup decrypt");
        File.Exists(dump).ShouldBeTrue();
        File.Exists(FieldMasterKey.PathIn(machine)).ShouldBeFalse("backup decrypt puts no key in place");

        var database = await ScriptedRestore.RunAsync(dump, Ct);
        try
        {
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
