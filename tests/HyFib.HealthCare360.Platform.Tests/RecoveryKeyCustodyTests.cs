using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.DataProtection;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Who holds the backups' recovery key (DED-16): two different people should have typed in the current key, one of them
/// with a copy outside the building, and someone should type it in every quarter, so a lost or unreadable copy is found
/// before the day of a restore. The key is never shown again: proving it is typing it in.
/// </summary>
public sealed class RecoveryKeyCustodyTests : IDisposable
{
    private const string KeyId = "7B1E0A44C2D9F3A8";
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Director = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 6, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_key_nobody_has_typed_in_is_held_by_nobody_anyone_can_name()
    {
        var warning = RecoveryKeyCustody.Warnings(KeyId, [], Now, HospitalTimeZone.Zone).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("backup.recovery_key_custodians", "warning"));
        warning.Message.ShouldStartWith($"Nobody has shown they hold recovery key {KeyId}");
    }

    /// <summary>The administrator who made the key typed it back; typing it in again later is still one person.</summary>
    [Fact]
    public void One_person_is_not_enough_however_often_they_type_it_in()
    {
        var custodians = RecoveryKeyCustody.Of([Proof(It, "itadmin", -30), Proof(It, "itadmin", -1)], KeyId);

        custodians.ShouldHaveSingleItem().ShouldBe(new RecoveryKeyCustodian(It, "itadmin", Now.AddDays(-1)));
        var warning = RecoveryKeyCustody.Warnings(KeyId, custodians, Now, HospitalTimeZone.Zone).ShouldHaveSingleItem();
        warning.Code.ShouldBe("backup.recovery_key_custodians");
        warning.Message.ShouldContain($"Only itadmin has shown they hold recovery key {KeyId}");
        warning.Message.ShouldContain("A second administrator");
    }

    [Fact]
    public void Two_people_who_typed_it_in_this_quarter_raise_nothing()
    {
        var custodians = RecoveryKeyCustody.Of([Proof(It, "itadmin", -60), Proof(Director, "director", -2)], KeyId);

        custodians.Select(c => c.Name).ShouldBe(["director", "itadmin"]);
        RecoveryKeyCustody.Warnings(KeyId, custodians, Now, HospitalTimeZone.Zone).ShouldBeEmpty();
    }

    /// <summary>The quarterly check missed: nobody has typed the key in for 90 days, so nobody knows its copies can still be read.</summary>
    [Theory]
    [InlineData(-89, false)]
    [InlineData(-91, true)]
    public void Nobody_typing_it_in_for_ninety_days_is_a_warning(int lastDays, bool warned)
    {
        var custodians = RecoveryKeyCustody.Of([Proof(It, "itadmin", lastDays - 10), Proof(Director, "director", lastDays)], KeyId);

        var warnings = RecoveryKeyCustody.Warnings(KeyId, custodians, Now, HospitalTimeZone.Zone);

        if (warned)
        {
            var warning = warnings.ShouldHaveSingleItem();
            (warning.Code, warning.Severity).ShouldBe(("backup.recovery_key_unchecked", "warning"));
            warning.Message.ShouldContain($"Nobody has typed in recovery key {KeyId} since {Now.AddDays(lastDays).ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}");
        }
        else
        {
            warnings.ShouldBeEmpty();
        }
    }

    /// <summary>Proofs of an earlier key do not count for the current one: a new key starts with nobody.</summary>
    [Fact]
    public void Proofs_of_another_key_do_not_count()
    {
        RecoveryKeyCustody.Of([Proof(It, "itadmin", -1, keyId: "0123456789ABCDEF"), Proof(Director, "director", -1, keyId: "0123456789ABCDEF")], KeyId)
            .ShouldBeEmpty();
    }

    /// <summary>Typing in the current key proves it and changes nothing; another key, or one copied wrong, proves nothing.</summary>
    [Fact]
    public void Only_the_current_key_typed_in_proves_it_and_nothing_changes()
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, "m") });
        directories.EnsureCreated();
        var protection = new EphemeralDataProtectionProvider();
        var store = new RecoveryKeyStore(directories, protection, new FieldProtector(directories, protection), TimeProvider.System);
        var admin = Guid.NewGuid();
        var created = store.Create(admin);
        store.Confirm(created.KeyId, created.Key, admin).IsSuccess.ShouldBeTrue();
        var before = File.ReadAllBytes(RecoveryKeyStore.PathIn(directories));

        // Any administrator who holds a copy, typed as people type it.
        store.Prove(created.Key.ToLowerInvariant().Replace('-', ' ')).Value.ShouldBe(created.KeyId);

        var other = store.Prove(BackupRecoveryKey.Format(BackupRecoveryKey.Create()));
        other.Error.ShouldNotBeNull().Fields.ShouldNotBeNull()["key"].ShouldHaveSingleItem().ShouldContain($"whose ID is {created.KeyId}");
        var typo = store.Prove(created.Key[..^1] + (created.Key[^1] == 'Z' ? 'Y' : 'Z'));
        typo.Error.ShouldNotBeNull().Fields.ShouldNotBeNull()["key"].ShouldBe(["The recovery key has a mistake in it. Check it against your copy."]);

        File.ReadAllBytes(RecoveryKeyStore.PathIn(directories)).ShouldBe(before);
        store.CurrentKeyId().ShouldBe(created.KeyId);
    }

    private static RecoveryKeyProof Proof(Guid user, string name, int days, string keyId = KeyId) =>
        new() { KeyId = keyId, UserId = user, UserName = name, ProvenAt = Now.AddDays(days) };
}
