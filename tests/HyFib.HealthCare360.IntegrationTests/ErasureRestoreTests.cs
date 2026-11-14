using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Portal;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Server;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The restore drill for erasure (DPDP Act 2023 s.8(7), s.12(3)): a patient's data is erased after the nightly backup,
/// the database is then restored from that backup, as Restore-HC360.ps1 restores it (the server CLI's <c>backup decrypt</c>,
/// then pg_restore into a new database), and the erasure comes back with it. The erasure ledger, kept outside the
/// database, is applied to the restored database, as the server does as it starts on it, and the erasure is carried out
/// again, once.
/// </summary>
public sealed class ErasureRestoreTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_erasure_made_after_a_backup_is_carried_out_again_when_that_backup_is_restored()
    {
        Assert.SkipUnless(ScriptedRestore.PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");

        // A patient with a nominee and a portal account, who asks for both to be erased.
        var (patient, phone) = await RegisterPatientAsync();
        var portal = await server.SignInToPortalAsync(phone);
        await portal.PutAsync("/api/v1/portal/privacy/nominee", new PortalNomineeRequest(patient.Id, "Ravi Rao", "Husband", "98" + Digits(8), null));
        var request = await portal.PostAsync<PortalRequestCreated>("/api/v1/portal/privacy/requests", new PortalDataRequestCreate(
            patient.Id, DataRequestType.Erasure, RequesterRelation.Self, null, "Please erase my nominee and close my account.", null));

        // The nightly backup is made before the officer erases.
        var (backup, recoveryKey) = await BackupAsync();
        var machine = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "hc360-tests", "erasure-restore-" + Guid.NewGuid().ToString("N")) });
        machine.EnsureCreated();
        try
        {
            var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            await officer.PostAsync<ErasureOutcome>($"/api/v1/privacy/requests/{request.Id}/erasure",
                new EraseDataRequest([ErasureService.NomineeItem, ErasureService.PortalAccount]));

            // The database is restored from the backup: the nominee and the portal account are back.
            await using var restored = await RestoreDatabaseAsync(backup, recoveryKey, machine);
            await using (var check = await ScopeOnAsync(restored.ConnectionString))
            {
                var db = check.ServiceProvider.GetRequiredService<HospitalDbContext>();
                (await db.Set<Nominee>().AnyAsync(n => n.PatientId == patient.Id, Ct)).ShouldBeTrue("the backup was made before the erasure");
                (await PortalAccountActiveAsync(db, phone)).ShouldBeTrue();
            }

            // The server, starting on the restored database, applies the ledger (ErasureLedgerSeeder runs this same replay).
            await using (var replay = await ScopeOnAsync(restored.ConnectionString))
            {
                var outcome = (await replay.ServiceProvider.GetRequiredService<ErasureLedgerReplay>().RunAsync(Ct)).ShouldNotBeNull();
                var item = outcome.Reapplied.Where(r => r.RequestId == request.Id).ShouldHaveSingleItem();
                item.Applied.ShouldBe([ErasureService.NomineeItem, ErasureService.PortalAccount], ignoreOrder: true);

                var db = replay.ServiceProvider.GetRequiredService<HospitalDbContext>();
                (await db.Set<Nominee>().AnyAsync(n => n.PatientId == patient.Id, Ct)).ShouldBeFalse("the erasure is carried out again");
                (await PortalAccountActiveAsync(db, phone)).ShouldBeFalse();
                (await db.Set<ErasureReplay>().AsNoTracking().CountAsync(r => r.RequestId == request.Id, Ct)).ShouldBe(1);
                (await db.Set<AuditEvent>().AsNoTracking().AnyAsync(a => a.Action == ErasureLedgerReplay.ReappliedAction && a.PatientId == patient.Id, Ct)).ShouldBeTrue();
            }

            // Started again, nothing more is done: each entry is applied once.
            await using (var again = await ScopeOnAsync(restored.ConnectionString))
            {
                var second = (await again.ServiceProvider.GetRequiredService<ErasureLedgerReplay>().RunAsync(Ct)).ShouldNotBeNull();
                second.Reapplied.ShouldNotContain(r => r.RequestId == request.Id);
            }

            // The live database already had it: replaying there changes nothing for this request.
            await using (var live = server.Services.CreateAsyncScope())
            {
                var outcome = (await live.ServiceProvider.GetRequiredService<ErasureLedgerReplay>().RunAsync(Ct)).ShouldNotBeNull();
                outcome.Reapplied.ShouldNotContain(r => r.RequestId == request.Id);
            }
        }
        finally
        {
            File.Delete(backup);
            Directory.Delete(machine.Root, recursive: true);
        }
    }

    /// <summary>
    /// The disaster: the Server Node's disk is gone, and the erasure ledger with it, and the backup is restored on new
    /// hardware from its second copy, as Restore-HC360.ps1 restores it (backup decrypt, pg_restore, backup restore). Every
    /// backup carries the ledger; the restore adds its erasures, and those of the ledger's copy beside the backup (which
    /// the second copy keeps), to the new server's ledger; and its start carries out again the erasure made after the
    /// backup, checked with the backup's audit seal key the restore installed. Before, the new server had no ledger at all.
    /// </summary>
    [Fact]
    public async Task An_erasure_made_after_a_backup_is_carried_out_again_on_new_hardware_restored_from_the_second_copy()
    {
        Assert.SkipUnless(ScriptedRestore.PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");
        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var before = await ErasureRequestedAsync();
        await officer.PostAsync<ErasureOutcome>($"/api/v1/privacy/requests/{before.Request.Id}/erasure", new EraseDataRequest([ErasureService.NomineeItem]));
        var after = await ErasureRequestedAsync();
        var (backup, recoveryKey) = await BackupAsync();
        var root = Path.Combine(Path.GetTempPath(), "hc360-tests", "erasure-disaster-" + Guid.NewGuid().ToString("N"));
        var machine = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(root, "new-hardware") });
        machine.EnsureCreated();
        try
        {
            await officer.PostAsync<ErasureOutcome>($"/api/v1/privacy/requests/{after.Request.Id}/erasure", new EraseDataRequest([ErasureService.NomineeItem]));

            // The backup carries the ledger as it was: the erasure before it, not the one after.
            var carried = await CarriedLedgerAsync(backup, recoveryKey);
            carried.ShouldContain(e => e.RequestId == before.Request.Id);
            carried.ShouldNotContain(e => e.RequestId == after.Request.Id);

            // What survives on the NAS: the backup's copy, and the ledger's copy beside it, as the server kept it up to date.
            var secondCopy = Path.Combine(root, "nas");
            Directory.CreateDirectory(secondCopy);
            var copied = Path.Combine(secondCopy, Path.GetFileName(backup));
            File.Copy(backup, copied);
            File.Copy(server.Services.GetRequiredService<ErasureLedger>().Path, Path.Combine(secondCopy, ErasureLedger.FileName));

            await using var restored = await RestoreDatabaseAsync(copied, recoveryKey, machine);
            (await CliAsync(recoveryKey, "backup", "restore", "--in", copied, "--data-dir", machine.Root)).ShouldBe(0, "backup restore");

            // The new server's ledger, read with the audit seal key the restore installed there, as its start reads it.
            var ledger = new ErasureLedger(machine, Microsoft.Extensions.Options.Options.Create(new PlatformOptions { DataDirectory = machine.Root }),
                new AuditSealKey(machine, HospitalDataProtection.CreateProvider(machine)), NullLogger<ErasureLedger>.Instance);
            var (entries, rejected) = ledger.Read();
            entries.ShouldContain(e => e.RequestId == before.Request.Id);
            entries.ShouldContain(e => e.RequestId == after.Request.Id);
            rejected.ShouldBe(0, "every line is sealed with the backup's audit seal key");

            await using (var start = await ScopeOnAsync(restored.ConnectionString))
            {
                var db = start.ServiceProvider.GetRequiredService<HospitalDbContext>();
                (await db.Set<Nominee>().AnyAsync(n => n.PatientId == after.Patient.Id, Ct)).ShouldBeTrue("the backup was made before the second erasure");

                var outcome = (await ActivatorUtilities.CreateInstance<ErasureLedgerReplay>(start.ServiceProvider, ledger).RunAsync(Ct)).ShouldNotBeNull();
                outcome.Reapplied.Where(r => r.RequestId == after.Request.Id).ShouldHaveSingleItem().Applied.ShouldBe([ErasureService.NomineeItem]);
                outcome.Reapplied.ShouldNotContain(r => r.RequestId == before.Request.Id, "the restored database has the erasure made before the backup");
                (await db.Set<Nominee>().AnyAsync(n => n.PatientId == after.Patient.Id, Ct)).ShouldBeFalse("the erasure is carried out again on the new hardware");
            }
        }
        finally
        {
            File.Delete(backup);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A patient with a nominee, who asks through the portal for the nominee to be erased.</summary>
    private async Task<(PatientCreated Patient, PortalRequestCreated Request)> ErasureRequestedAsync()
    {
        var (patient, phone) = await RegisterPatientAsync();
        var portal = await server.SignInToPortalAsync(phone);
        await portal.PutAsync("/api/v1/portal/privacy/nominee", new PortalNomineeRequest(patient.Id, "Ravi Rao", "Husband", "98" + Digits(8), null));
        var request = await portal.PostAsync<PortalRequestCreated>("/api/v1/portal/privacy/requests", new PortalDataRequestCreate(
            patient.Id, DataRequestType.Erasure, RequesterRelation.Self, null, "Please erase my nominee.", null));
        return (patient, request);
    }

    /// <summary>The erasure ledger a backup carries, read as the server reads a ledger: lines this server's key sealed.</summary>
    private async Task<IReadOnlyList<ErasureLedgerEntry>> CarriedLedgerAsync(string backup, byte[] recoveryKey)
    {
        await using var input = File.OpenRead(backup);
        await using var payload = await BackupEncryption.OpenAsync(input, recoveryKey, Ct);
        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), Ct);
        var ledger = server.Services.GetRequiredService<ErasureLedger>();
        return [.. ErasureLedgerFiles.Lines(contents.ErasureLedger.ShouldNotBeNull("every backup carries the erasure ledger"))
            .Select(line => JsonSerializer.Deserialize<ErasureLedgerEntry>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .OfType<ErasureLedgerEntry>()
            .Where(ledger.Verifies)];
    }

    /// <summary>The server CLI as the script runs it: the recovery key in HC360_RECOVERY_KEY for the call only.</summary>
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

    /// <summary>A scope of the test server whose database is <paramref name="connectionString"/>: every service in it reads and writes there.</summary>
    private async Task<AsyncServiceScope> ScopeOnAsync(string connectionString)
    {
        var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Database.SetConnectionString(connectionString);
        await Task.CompletedTask;
        return scope;
    }

    private static async Task<bool> PortalAccountActiveAsync(HospitalDbContext db, string phone)
    {
        var e164 = Phone.Normalize(phone);
        return await db.Set<User>().AnyAsync(u => u.Type == UserType.Patient && u.Phone == e164 && u.IsActive, Ct);
    }

    private async Task<(string File, byte[] RecoveryKey)> BackupAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(BackupKind.Manual, null, Ct);
        run.Status.ShouldBe(BackupStatus.Succeeded, run.Error);
        var key = server.Services.GetRequiredService<RecoveryKeyStore>().Current();
        return (Path.Combine(server.Services.GetRequiredService<DataDirectories>().Backups, run.FileName!), key.Key);
    }

    /// <summary>
    /// The database steps of Restore-HC360.ps1: <c>backup decrypt</c> writes the dump, and pg_restore fills a new database as
    /// the script runs it, as its owner (<see cref="ScriptedRestore"/>).
    /// </summary>
    private static async Task<ScriptedRestore> RestoreDatabaseAsync(string backup, byte[] recoveryKey, DataDirectories machine)
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

        return await ScriptedRestore.RunAsync(dump, Ct);
    }

    private async Task<(PatientCreated Patient, string Phone)> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var phone = "9" + Digits(9);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: "Uma", MiddleName: null, LastName: "Restore" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return (patient, phone);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
