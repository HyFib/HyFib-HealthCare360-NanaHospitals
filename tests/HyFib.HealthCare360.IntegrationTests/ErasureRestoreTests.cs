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
