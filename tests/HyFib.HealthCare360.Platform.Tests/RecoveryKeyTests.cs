using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.DataProtection;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// ASVS V11.1.1, V13.3.2: backups have a recovery key of their own. It is written down in a form that catches copying
/// mistakes, and a new key replaces the current one only once the administrator it was shown to types it back.
/// </summary>
public sealed class RecoveryKeyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly EphemeralDataProtectionProvider _protection = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 6, 0, 0, TimeSpan.Zero));
    private readonly Guid _admin = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_key_is_written_as_fourteen_groups_of_four_without_easily_confused_letters()
    {
        var key = BackupRecoveryKey.Create();

        var text = BackupRecoveryKey.Format(key);

        var groups = text.Split('-');
        groups.Length.ShouldBe(14);
        groups.ShouldAllBe(g => g.Length == 4);
        text.Replace("-", string.Empty, StringComparison.Ordinal).ShouldAllBe(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c));
        BackupRecoveryKey.TryParse(text, out var parsed, out var problem).ShouldBeTrue();
        problem.ShouldBeNull();
        parsed.ShouldBe(key);
    }

    [Fact]
    public void A_key_reads_back_however_it_is_typed()
    {
        var key = BackupRecoveryKey.Create();
        var text = BackupRecoveryKey.Format(key);

        // Lower case, spaces for dashes, and O, I and L for 0, 1 and 1, as someone may copy them by hand.
        var typed = text.ToLowerInvariant().Replace('-', ' ').Replace('0', 'o').Replace('1', 'l');

        BackupRecoveryKey.TryParse(typed, out var parsed, out _).ShouldBeTrue();
        parsed.ShouldBe(key);
        BackupRecoveryKey.TryParse($"  {text.Replace("-", string.Empty, StringComparison.Ordinal)}\n", out parsed, out _).ShouldBeTrue();
        parsed.ShouldBe(key);
    }

    [Fact]
    public void A_mistake_in_a_copy_is_caught()
    {
        // A fixed key, so the result does not depend on chance: a random key would let one change in a million pass.
        var text = BackupRecoveryKey.Format(Enumerable.Range(0, 32).Select(i => (byte)((i * 37) + 11)).ToArray());
        var characters = text.ToCharArray();

        // One character copied wrong, anywhere, including in the check group.
        for (var i = 0; i < characters.Length; i++)
        {
            if (characters[i] == '-')
            {
                continue;
            }

            var copy = (char[])characters.Clone();
            copy[i] = copy[i] == 'Z' ? 'Y' : 'Z';
            BackupRecoveryKey.TryParse(new string(copy), out _, out var problem).ShouldBeFalse();
            problem.ShouldBe("The recovery key has a mistake in it. Check it against your copy.");
        }

        // Two groups swapped.
        var groups = text.Split('-');
        (groups[3], groups[4]) = (groups[4], groups[3]);
        BackupRecoveryKey.TryParse(string.Join('-', groups), out _, out _).ShouldBeFalse();

        // A letter the key never has.
        BackupRecoveryKey.TryParse("U" + text[1..], out _, out var notInKeys).ShouldBeFalse();
        notInKeys.ShouldBe("The recovery key has a mistake in it. Check it against your copy.");
    }

    [Fact]
    public void The_original_key_is_still_read_in_the_form_it_was_shown()
    {
        var original = BackupRecoveryKey.Original(RandomNumberGenerator.GetBytes(32));

        BackupRecoveryKey.TryParse(Convert.ToBase64String(original), out var parsed, out _).ShouldBeTrue();

        parsed.ShouldBe(original);
    }

    [Theory]
    [InlineData("", "Enter the recovery key.")]
    [InlineData("not a key", "That is not an HC360 recovery key.")]
    [InlineData("AAAA-BBBB-CCCC", "That is not an HC360 recovery key.")]
    public void Something_else_is_not_taken_for_a_key(string text, string problemStart)
    {
        BackupRecoveryKey.TryParse(text, out var key, out var problem).ShouldBeFalse();

        key.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldStartWith(problemStart);
    }

    [Fact]
    public void A_key_id_names_a_key_without_revealing_it()
    {
        var key = BackupRecoveryKey.Create();

        var id = BackupRecoveryKey.Id(key);

        id.Length.ShouldBe(16);
        BackupRecoveryKey.Id(key).ShouldBe(id);
        BackupRecoveryKey.Id(BackupRecoveryKey.Create()).ShouldNotBe(id);
        Convert.ToHexString(key).ShouldNotContain(id);
    }

    [Fact]
    public void Until_a_key_is_made_backups_use_the_original_key()
    {
        var (store, protector) = Store("m");

        var current = store.Current();

        store.IsOriginal.ShouldBeTrue();
        current.Original.ShouldBeTrue();
        current.Key.ShouldBe(BackupRecoveryKey.Original(protector.ExportMasterKey()));
        current.KeyId.ShouldBe(BackupRecoveryKey.Id(current.Key));
    }

    [Fact]
    public void A_new_key_replaces_the_current_one_only_once_it_is_typed_back()
    {
        var (store, _) = Store("m");
        var original = store.Current().KeyId;

        var created = store.Create(_admin);

        created.ExpiresAt.ShouldBe(_clock.Now + RecoveryKeyStore.ConfirmWithin);
        store.Current().KeyId.ShouldBe(original);
        var wrong = store.Confirm(created.KeyId, BackupRecoveryKey.Format(BackupRecoveryKey.Create()), _admin);
        wrong.Error.ShouldNotBeNull().Fields.ShouldNotBeNull()["key"].ShouldBe(["This is not the new key. Go back, check your copy against it, and type it again."]);
        store.Current().KeyId.ShouldBe(original);

        var confirmed = store.Confirm(created.KeyId, created.Key.ToLowerInvariant(), _admin);

        confirmed.Value.ShouldBe(original);
        var current = store.Current();
        (current.KeyId, current.Original).ShouldBe((created.KeyId, false));
        current.Since.ShouldNotBeNull();
        store.IsOriginal.ShouldBeFalse();
        BackupRecoveryKey.Format(current.Key).ShouldBe(created.Key);
    }

    [Fact]
    public void A_confirmed_key_is_kept_on_disk_wrapped_for_this_machine()
    {
        var (store, protector) = Store("m");
        var created = store.Create(_admin);
        store.Confirm(created.KeyId, created.Key, _admin).IsSuccess.ShouldBeTrue();

        var file = File.ReadAllBytes(Path.Combine(Machine("m").Keys, RecoveryKeyStore.FileName));
        BackupRecoveryKey.TryParse(created.Key, out var key, out _).ShouldBeTrue();
        file.AsSpan().IndexOf(key).ShouldBe(-1);

        // After a restart.
        new RecoveryKeyStore(Machine("m"), _protection, protector, _clock).Current().KeyId.ShouldBe(created.KeyId);
    }

    [Fact]
    public void A_key_waiting_to_be_typed_back_is_kept_wrapped_beside_the_current_one_until_it_is()
    {
        var (store, protector) = Store("m");
        var created = store.Create(_admin);
        var pending = Path.Combine(Machine("m").Keys, RecoveryKeyStore.PendingFileName);

        // In the keys folder, which several servers share (SCAL-01), not in this one's memory: a restart keeps it too.
        BackupRecoveryKey.TryParse(created.Key, out var key, out _).ShouldBeTrue();
        File.ReadAllBytes(pending).AsSpan().IndexOf(key).ShouldBe(-1);
        store.IsOriginal.ShouldBeTrue();
        var restarted = new RecoveryKeyStore(Machine("m"), _protection, protector, _clock);

        restarted.Confirm(created.KeyId, created.Key, _admin).IsSuccess.ShouldBeTrue();

        File.Exists(pending).ShouldBeFalse();
        store.Current().KeyId.ShouldBe(created.KeyId);
    }

    [Fact]
    public void A_key_no_longer_waiting_is_removed_when_it_is_typed_back_too_late()
    {
        var (store, _) = Store("m");
        var created = store.Create(_admin);
        _clock.Now += RecoveryKeyStore.ConfirmWithin;

        NotWaiting(store.Confirm(created.KeyId, created.Key, _admin));

        File.Exists(Path.Combine(Machine("m").Keys, RecoveryKeyStore.PendingFileName)).ShouldBeFalse();
    }

    [Fact]
    public void Only_the_administrator_it_was_shown_to_can_confirm_a_key_within_half_an_hour()
    {
        var (store, _) = Store("m");
        var created = store.Create(_admin);

        NotWaiting(store.Confirm(created.KeyId, created.Key, Guid.NewGuid()));

        _clock.Now += RecoveryKeyStore.ConfirmWithin;
        NotWaiting(store.Confirm(created.KeyId, created.Key, _admin));
        store.IsOriginal.ShouldBeTrue();
    }

    [Fact]
    public void A_key_made_again_drops_the_one_before()
    {
        var (store, _) = Store("m");
        var first = store.Create(_admin);
        var second = store.Create(_admin);

        NotWaiting(store.Confirm(first.KeyId, first.Key, _admin));

        store.Confirm(second.KeyId, second.Key, _admin).IsSuccess.ShouldBeTrue();
        store.Current().KeyId.ShouldBe(second.KeyId);

        // Once confirmed, it is no longer waiting either.
        NotWaiting(store.Confirm(second.KeyId, second.Key, _admin));
    }

    private static void NotWaiting(Result<string> result) => result.Error.ShouldNotBeNull().Code.ShouldBe("backup.recovery_key_not_waiting");

    private (RecoveryKeyStore Store, FieldProtector Protector) Store(string machine)
    {
        var directories = Machine(machine);
        var protector = new FieldProtector(directories, _protection);
        return (new RecoveryKeyStore(directories, _protection, protector, _clock), protector);
    }

    private DataDirectories Machine(string name)
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, name) });
        directories.EnsureCreated();
        return directories;
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
