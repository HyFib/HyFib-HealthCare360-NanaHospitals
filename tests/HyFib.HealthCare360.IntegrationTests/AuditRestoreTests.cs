using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A restore puts the audit chain back to its backup's end, while the anchors written outside the database since go on
/// naming the positions after it, as the chain had them before. Every check after a restore found the chain cut short, or
/// made again, and System health raised a critical alert every day for good. Now HC360's first start on the restored
/// database (the restore marker beside it) records the restore: anchored outside the database with this server's seal, and
/// in the audit trail as a critical entry naming the point restored to. The check accepts that one break, and no other.
/// </summary>
public sealed class AuditRestoreTests(HospitalServer server)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task After_a_restore_the_chain_check_accepts_the_break_at_the_point_restored_to_and_no_other()
    {
        Assert.SkipUnless(ScriptedRestore.PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");
        var machine = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "hc360-tests", "audit-restore-" + Guid.NewGuid().ToString("N")) });
        machine.EnsureCreated();
        string? backup = null;
        try
        {
            await SealAndAnchorAsync();
            (backup, var recoveryKey) = await BackupAsync();

            // After the backup the server goes on: more records, sealed, and the chain's end anchored outside the database.
            await RecordAsync("After the backup");
            await SealAndAnchorAsync();

            // The anchor file is kept outside the database and its backups: on the machine restored onto, it is the one the
            // server kept (or its copy, put back).
            Directory.CreateDirectory(Path.GetDirectoryName(machine.AuditAnchors)!);
            File.Copy(server.Services.GetRequiredService<AuditAnchors>().Path, machine.AuditAnchors);
            var anchors = new AuditAnchors(machine, NullLogger<AuditAnchors>.Instance);
            var key = server.Services.GetRequiredService<AuditSealKey>();
            await using var restored = await ScriptedRestore.RunAsync(await DecryptAsync(backup, recoveryKey, machine), Ct);

            await using var scope = OnDatabase(restored.ConnectionString);
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var chain = new AuditChainService(db, key, anchors, TimeProvider.System);
            var end = (await db.Set<AuditEvent>().AsNoTracking().MaxAsync(e => e.ChainIndex, Ct)).ShouldNotBeNull();
            var cut = await chain.VerifyAsync(Ct);
            cut.Valid.ShouldBeFalse("the restored chain ends before the anchors written after the backup");
            cut.Message.ShouldNotBeNull().ShouldContain("was anchored outside the database");

            // HC360 starts on the restored database, with the marker Restore-HC360.ps1 left beside it.
            var restoredAt = new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
            await File.WriteAllTextAsync(Path.Combine(machine.Root, RestoreMarker.FileName), JsonSerializer.Serialize(
                new { restoredAt, backupFile = backup, backupTakenAt = (DateTimeOffset?)null }, Web), Ct);
            var mark = new NumberSeriesHighWaterMark(machine, Microsoft.Extensions.Options.Options.Create(new PlatformOptions { DataDirectory = machine.Root }), key,
                NullLogger<NumberSeriesHighWaterMark>.Instance);
            var check = ActivatorUtilities.CreateInstance<NumberSeriesStartupCheck>(scope.ServiceProvider, mark,
                new RestoreMarker(machine, TimeProvider.System, NullLogger<RestoreMarker>.Instance),
                new AuditRestores(anchors, key, NullLogger<AuditRestores>.Instance));
            await check.SeedAsync(db, Ct);
            scope.ServiceProvider.GetRequiredService<NumberSeriesStartupStatus>().Failure.ShouldBeNull();
            db.ChangeTracker.Clear();

            // The restore, in the audit trail and outside the database, names the point restored to: the backup's chain end.
            var entry = await db.Set<AuditEvent>().AsNoTracking().SingleAsync(e => e.Action == AuditRestores.RestoredAction, Ct);
            (entry.Severity, entry.Category, entry.EntityId).ShouldBe((AuditSeverity.Critical, AuditCategories.Security, end.ToString(CultureInfo.InvariantCulture)));
            entry.Summary.ShouldNotBeNull().ShouldContain(Path.GetFileName(backup));
            var restore = anchors.Read().Anchors[^1];
            var restoreMark = restore.Restore.ShouldNotBeNull();
            (restore.ChainIndex, restoreMark.RestoredAt, restoreMark.Backup).ShouldBe((end, restoredAt, Path.GetFileName(backup)));

            // The server goes on sealing and anchoring on the restored database: the chain checks, the one break accepted.
            await SealAsync(chain);
            (await chain.AnchorIfDueAsync(TimeSpan.Zero, Ct)).ShouldNotBeNull().ChainIndex.ShouldBeGreaterThan(end);
            var accepted = await chain.VerifyAsync(Ct);
            accepted.Valid.ShouldBeTrue(accepted.Message);
            accepted.Message.ShouldNotBeNull().ShouldContain(string.Create(CultureInfo.InvariantCulture, $"restored on {restoredAt:dd MMM yyyy} to position {end:N0}"));

            // Any other break is still found: an anchor written since the restore that the chain does not match, ...
            var file = await File.ReadAllTextAsync(machine.AuditAnchors, Ct);
            var last = anchors.Last().ShouldNotBeNull();
            anchors.Append(last with { AnchoredAt = DateTimeOffset.UtcNow, Hash = new string('A', 64) });
            var remade = await chain.VerifyAsync(Ct);
            remade.Valid.ShouldBeFalse();
            remade.Message.ShouldBe($"The audit record at position {last.ChainIndex} is not the one anchored outside the database. The chain has been made again.");

            // ... and a restore mark written after it, to the same point, without this server's seal sets nothing aside.
            anchors.Append(restore with { AnchoredAt = DateTimeOffset.UtcNow, Restore = restoreMark with { Seal = new string('B', 64) } });
            (await chain.VerifyAsync(Ct)).Message.ShouldBe(remade.Message, "the anchor that does not match is still checked");
            await File.WriteAllTextAsync(machine.AuditAnchors, file, Ct);
            (await chain.VerifyAsync(Ct)).Valid.ShouldBeTrue();
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

    /// <summary>Records an audit event on the test server.</summary>
    private async Task RecordAsync(string summary)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditCategories.PhiAccess,
            Action = "patient.accessed",
            EntityType = "Patient",
            PatientId = Guid.CreateVersion7(),
            UserName = "doctor",
            Summary = summary,
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Seals every record written so far on the test server, and anchors the chain's end, as its sealer does.</summary>
    private async Task SealAndAnchorAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var chain = scope.ServiceProvider.GetRequiredService<AuditChainService>();
        await SealAsync(chain);
        await chain.AnchorIfDueAsync(TimeSpan.Zero, Ct);
    }

    private static async Task SealAsync(AuditChainService chain)
    {
        for (var attempt = 0; await chain.SealPendingAsync(Ct) > 0; attempt++)
        {
            attempt.ShouldBeLessThan(1000, "The audit records were not sealed.");
        }
    }

    /// <summary>A backup as Administration › Backups › Back up now makes it, and the recovery key that opens it.</summary>
    private async Task<(string File, byte[] RecoveryKey)> BackupAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(BackupKind.Manual, null, Ct);
        run.Status.ShouldBe(BackupStatus.Succeeded, run.Error);
        return (Path.Combine(server.Services.GetRequiredService<DataDirectories>().Backups, run.FileName!),
            server.Services.GetRequiredService<RecoveryKeyStore>().Current().Key);
    }

    /// <summary>The script's first step: the server CLI's <c>backup decrypt</c> writes the database dump.</summary>
    private static async Task<string> DecryptAsync(string backup, byte[] recoveryKey, DataDirectories machine)
    {
        var dump = Path.Combine(machine.Temp, "restore.dump");
        Environment.SetEnvironmentVariable("HC360_RECOVERY_KEY", BackupRecoveryKey.Format(recoveryKey));
        try
        {
            (await ServerCli.RunAsync(["backup", "decrypt", "--in", backup, "--out", dump, "--data-dir", machine.Root])).ShouldBe(0, "backup decrypt");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HC360_RECOVERY_KEY", null);
        }

        return dump;
    }

    /// <summary>A scope of the test server whose database is <paramref name="connectionString"/>: every service in it reads and writes there.</summary>
    private AsyncServiceScope OnDatabase(string connectionString)
    {
        var scope = server.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.SetConnectionString(connectionString);
        return scope;
    }
}
