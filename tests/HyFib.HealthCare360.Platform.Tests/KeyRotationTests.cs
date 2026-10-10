using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS SEC-07, ASVS V11.1.1, V11.2.2: the data encryption key and the token signing key rotate. After a rotation new data
/// uses the new key, everything encrypted before stays readable, and blind indexes and the original recovery key do not
/// change; backups carry every key, so a restore reads data encrypted with any of them. An earlier key can be retired,
/// and a backup made before still restores and brings it back.
/// </summary>
public sealed class KeyRotationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly EphemeralDataProtectionProvider _protection = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void After_a_rotation_new_values_name_the_new_key_and_earlier_ones_stay_readable()
    {
        var protector = new FieldProtector(Machine("m"), _protection);
        var before = protector.Protect("12-3456-7890-1234");
        var bytesBefore = protector.ProtectBytes([1, 2, 3], "files");
        var original = protector.CurrentKeyId;

        var id = protector.Rotate();
        var after = protector.Protect("12-3456-7890-1234");
        var bytesAfter = protector.ProtectBytes([1, 2, 3], "files");

        before.ShouldStartWith("v1.");
        after.ShouldStartWith($"v2.{id}.");
        (protector.CurrentKeyId, protector.OriginalKeyId, protector.CurrentPrefix).ShouldBe((id, original, $"v2.{id}."));
        id.ShouldNotBe(original);
        protector.Unprotect(before).ShouldBe("12-3456-7890-1234");
        protector.Unprotect(after).ShouldBe("12-3456-7890-1234");
        (protector.KeyIdOf(bytesBefore), protector.KeyIdOf(bytesAfter)).ShouldBe((original, id));
        protector.UnprotectBytes(bytesBefore, "files").ShouldBe([1, 2, 3]);
        protector.UnprotectBytes(bytesAfter, "files").ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Fixed_blind_indexes_and_the_original_recovery_key_do_not_change()
    {
        var protector = new FieldProtector(Machine("m"), _protection);
        var index = protector.BlindIndex("123456789012");
        var originalRecoveryKey = BackupRecoveryKey.Original(protector.ExportMasterKey());
        var fingerprint = protector.Fingerprint;

        protector.Rotate();
        protector.Rotate();

        protector.FixedBlindIndex("123456789012").ShouldBe(index);
        BackupRecoveryKey.Original(protector.ExportMasterKey()).ShouldBe(originalRecoveryKey);
        protector.Fingerprint.ShouldBe(fingerprint);
    }

    [Fact]
    public void A_blind_index_names_the_key_it_was_made_with_and_is_found_under_every_key()
    {
        var protector = new FieldProtector(Machine("m"), _protection);
        var original = protector.BlindIndex("91234567890123");

        var first = protector.Rotate();
        var afterFirst = protector.BlindIndex("91234567890123");
        var second = protector.Rotate();
        var afterSecond = protector.BlindIndex("91234567890123");

        // The original key's index is 64 hex digits, as before keys rotated; a later key's starts with its id, and fits the
        // same 64-character column.
        original.ShouldMatch("^[0-9A-F]{64}$");
        afterFirst.ShouldStartWith(first + ".");
        afterSecond.ShouldStartWith(second + ".");
        afterSecond.Length.ShouldBeLessThanOrEqualTo(64);
        afterSecond.ShouldNotBe(afterFirst);

        // A lookup finds a row whichever key its index was made with.
        protector.BlindIndexes("91234567890123").ShouldBe([original, afterFirst, afterSecond], ignoreOrder: true);
        protector.BlindIndexes("91234567890124").ShouldNotContain(afterSecond);

        // The Aadhaar index stays on the original key.
        protector.FixedBlindIndex("91234567890123").ShouldBe(original);
    }

    [Fact]
    public void The_ring_and_its_current_key_are_kept_on_disk()
    {
        var machine = Machine("m");
        var first = new FieldProtector(machine, _protection);
        var v1 = first.Protect("policy 1");
        var firstRotation = first.Rotate();
        var v2 = first.Protect("policy 2");
        var secondRotation = first.Rotate();
        var v3 = first.Protect("policy 3");

        // The service restarts.
        var restarted = new FieldProtector(machine, _protection);

        restarted.CurrentKeyId.ShouldBe(secondRotation);
        restarted.Keys.Select(k => k.Id).ShouldBe([first.OriginalKeyId, firstRotation, secondRotation], ignoreOrder: true);
        restarted.Keys.ShouldContain(k => k.Original && k.Id == first.OriginalKeyId);
        restarted.Keys.Single(k => k.Current).Id.ShouldBe(secondRotation);
        new[] { restarted.Unprotect(v1), restarted.Unprotect(v2), restarted.Unprotect(v3) }.ShouldBe(["policy 1", "policy 2", "policy 3"]);
    }

    [Fact]
    public void Data_encrypted_with_a_key_the_server_does_not_have_is_refused_with_its_id()
    {
        var other = new FieldProtector(Machine("other"), _protection);
        var id = other.Rotate();
        var foreign = other.Protect("secret");

        var ex = Should.Throw<CryptographicException>(() => new FieldProtector(Machine("m"), _protection).Unprotect(foreign));

        ex.Message.ShouldContain(id);
    }

    [Fact]
    public void A_key_file_that_does_not_match_its_name_is_refused()
    {
        var machine = Machine("m");
        new FieldProtector(machine, _protection).Rotate();
        var keyFile = Directory.GetFiles(FieldKeyRing.DirectoryIn(machine), "*.key").Single();
        File.Move(keyFile, Path.Combine(FieldKeyRing.DirectoryIn(machine), "0000000000000000.key"));

        Should.Throw<CryptographicException>(() => new FieldProtector(machine, _protection).Protect("x"));
    }

    [Fact]
    public void A_retired_key_leaves_the_ring_and_the_disk()
    {
        var machine = Machine("m");
        var protector = new FieldProtector(machine, _protection);
        var earlier = protector.Rotate();
        var left = protector.Protect("12-3456-7890-1234");
        var current = protector.Rotate();
        var moved = protector.Protect("98-7654-3210-9876");

        protector.Retire(earlier);

        protector.Keys.Select(k => k.Id).ShouldBe([protector.OriginalKeyId, current], ignoreOrder: true);
        File.Exists(Path.Combine(FieldKeyRing.DirectoryIn(machine), earlier + ".key")).ShouldBeFalse();
        protector.Unprotect(moved).ShouldBe("98-7654-3210-9876");

        // Whatever was left on it can no longer be read here: retiring is only for a key nothing is encrypted with.
        Should.Throw<CryptographicException>(() => protector.Unprotect(left)).Message.ShouldContain(earlier);

        // A restart does not bring it back.
        var restarted = new FieldProtector(machine, _protection);
        restarted.Keys.Select(k => k.Id).ShouldBe([protector.OriginalKeyId, current], ignoreOrder: true);
        restarted.CurrentKeyId.ShouldBe(current);
    }

    [Fact]
    public void The_original_key_and_the_current_one_are_never_retired()
    {
        var machine = Machine("m");
        var protector = new FieldProtector(machine, _protection);
        var current = protector.Rotate();

        Should.Throw<InvalidOperationException>(() => protector.Retire(protector.OriginalKeyId)).Message.ShouldContain("original key");
        Should.Throw<InvalidOperationException>(() => protector.Retire(current)).Message.ShouldContain("current key");
        Should.Throw<InvalidOperationException>(() => protector.Retire("0123456789ABCDEF")).Message.ShouldContain("not in the ring");
        Should.Throw<InvalidOperationException>(() => FieldKeyRing.Remove(machine, current));
        Should.Throw<ArgumentException>(() => FieldKeyRing.Remove(machine, "../field-master"));

        new FieldProtector(machine, _protection).Keys.Count.ShouldBe(2);
    }

    [Fact]
    public void The_ring_knows_when_its_current_key_became_current()
    {
        var protector = new FieldProtector(Machine("m"), _protection);
        protector.CurrentSince.ShouldBeNull("the original key has been current since installation");

        protector.Rotate();

        protector.CurrentSince.ShouldNotBeNull().ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_backup_made_before_a_key_was_retired_restores_and_brings_the_key_back()
    {
        var machine = Machine("m");
        var protector = new FieldProtector(machine, _protection);
        var earlier = protector.Rotate();
        var value = protector.Protect("12-3456-7890-1234");
        var current = protector.Rotate();
        var (backup, recoveryKey) = await BackupWithRingAsync(machine, protector);

        protector.Retire(earlier);
        await BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(machine.Temp, "r.dump"), machine, _protection, TimeProvider.System, CancellationToken.None);

        var restored = new FieldProtector(machine, _protection);
        restored.Keys.Select(k => k.Id).ShouldBe([protector.OriginalKeyId, earlier, current], ignoreOrder: true);
        restored.CurrentKeyId.ShouldBe(current);
        restored.Unprotect(value).ShouldBe("12-3456-7890-1234");
    }

    [Fact]
    public async Task A_backup_carries_every_key_and_restores_data_encrypted_with_any_of_them()
    {
        var oldServer = Machine("old");
        var oldProtector = new FieldProtector(oldServer, _protection);
        var beforeRotation = oldProtector.Protect("12-3456-7890-1234");
        oldProtector.Rotate();
        var afterRotation = oldProtector.Protect("98-7654-3210-9876");
        var current = oldProtector.Rotate();
        var document = RandomNumberGenerator.GetBytes(64);
        Directory.CreateDirectory(Path.Combine(oldServer.Files, "2026", "10"));
        File.WriteAllBytes(Path.Combine(oldServer.Files, "2026", "10", "scan.bin"), oldProtector.ProtectBytes(document, "files"));
        var (backup, recoveryKey) = await BackupWithRingAsync(oldServer, oldProtector);

        var newServer = Machine("new");
        var newProtection = new EphemeralDataProtectionProvider();
        var result = await BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(newServer.Temp, "r.dump"), newServer, newProtection, TimeProvider.System, CancellationToken.None);

        result.RingKeysInstalled.ShouldBe(2);
        result.Manifest!.CurrentKey.ShouldBe(current);
        var newProtector = new FieldProtector(newServer, newProtection);
        newProtector.CurrentKeyId.ShouldBe(current);
        newProtector.Unprotect(beforeRotation).ShouldBe("12-3456-7890-1234");
        newProtector.Unprotect(afterRotation).ShouldBe("98-7654-3210-9876");
        newProtector.UnprotectBytes(File.ReadAllBytes(Path.Combine(newServer.Files, "2026", "10", "scan.bin")), "files").ShouldBe(document);
        newProtector.Protect("new").ShouldStartWith($"v2.{current}.");
    }

    [Fact]
    public async Task A_backup_missing_a_key_its_manifest_lists_is_refused()
    {
        var server = Machine("old");
        var protector = new FieldProtector(server, _protection);
        protector.Rotate();
        var masterKey = protector.ExportMasterKey();
        var ring = protector.ExportRing();
        var manifest = new BackupManifest(BackupArchive.Format, "Manual", DateTimeOffset.UtcNow, "1.0.0", "hc360", [], false, 0, 0,
            protector.Fingerprint, ring.Keys.Select(FieldMasterKey.Fingerprint).ToList(), ring.Current);
        using var payload = new MemoryStream();
        var dump = Path.Combine(server.Temp, "db.dump");
        File.WriteAllBytes(dump, "PGDMP"u8.ToArray());
        await BackupArchive.WriteAsync(payload, manifest, masterKey, [], dump, server.Files, [], CancellationToken.None);
        payload.Position = 0;

        var ex = await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(payload, new BackupReadTargets(), CancellationToken.None));

        ex.Message.ShouldContain("an encryption key its manifest lists is missing");
    }

    [Fact]
    public void After_a_signing_key_rotation_tokens_signed_before_stay_valid_until_they_expire()
    {
        var machine = Machine("m");
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var options = Options.Create(new PlatformOptions { Security = { AccessTokenMinutes = 10 } });
        var store = new SigningKeyStore(machine, _protection, options, clock);
        var before = store.Key.KeyId;

        var after = store.Rotate();

        after.ShouldNotBe(before);
        store.Key.KeyId.ShouldBe(after);
        Ids(store).ShouldBe([after, before]);

        // Only one previous key is kept, so a second rotation waits until its tokens have expired.
        store.NextRotationAt.ShouldBe(clock.Now + TimeSpan.FromMinutes(11));
        Should.Throw<InvalidOperationException>(store.Rotate);

        // A restart within the overlap keeps the previous key; past it, only the current one is accepted.
        clock.Now += TimeSpan.FromMinutes(5);
        Ids(new SigningKeyStore(machine, _protection, options, clock)).ShouldBe([after, before]);
        clock.Now += TimeSpan.FromMinutes(7);
        Ids(store).ShouldBe([after]);
        Ids(new SigningKeyStore(machine, _protection, options, clock)).ShouldBe([after]);
        store.NextRotationAt.ShouldBeNull();
        Ids(store).ShouldNotContain(store.Rotate());
        Ids(store).ShouldBe([store.Key.KeyId, after]);
    }

    [Fact]
    public async Task A_backup_made_before_any_rotation_makes_the_original_key_current_again()
    {
        // A backup of a server that never rotated.
        var oldServer = Machine("old");
        var oldProtector = new FieldProtector(oldServer, _protection);
        var value = oldProtector.Protect("12-3456-7890-1234");
        var (backup, recoveryKey) = await BackupWithRingAsync(oldServer, oldProtector);

        // Restored on a machine that had rotated its own keys (perhaps because one was exposed).
        var machine = Machine("new");
        var own = new FieldProtector(machine, _protection);
        own.Rotate();
        await BackupRestore.RestoreAsync(backup, recoveryKey, Path.Combine(machine.Temp, "r.dump"), machine, _protection, TimeProvider.System, CancellationToken.None);

        var restored = new FieldProtector(machine, _protection);
        restored.CurrentKeyId.ShouldBe(oldProtector.CurrentKeyId);
        restored.Protect("new").ShouldStartWith("v1.");
        restored.Unprotect(value).ShouldBe("12-3456-7890-1234");
    }

    private static string?[] Ids(SigningKeyStore store) => store.ValidationKeys().Select(k => k.KeyId).ToArray();

    private DataDirectories Machine(string name)
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, name) });
        directories.EnsureCreated();
        return directories;
    }

    /// <summary>A backup as the backup service writes it: the original key, the ring, and the documents.</summary>
    private static async Task<(string File, byte[] RecoveryKey)> BackupWithRingAsync(DataDirectories machine, FieldProtector protector)
    {
        var recoveryKey = BackupRecoveryKey.Original(protector.ExportMasterKey());
        var documents = BackupArchive.ListDocuments(machine.Files);
        var ring = protector.ExportRing();
        var manifest = new BackupManifest(BackupArchive.Format, "Manual", DateTimeOffset.UtcNow, "1.0.0", "hc360", [], true,
            documents.Count, documents.Sum(d => d.Length), protector.Fingerprint, ring.Keys.Select(FieldMasterKey.Fingerprint).ToList(), ring.Current);
        var dump = Path.Combine(machine.Temp, "db.dump");
        File.WriteAllBytes(dump, [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(10)]);
        var file = Path.Combine(machine.Backups, "ring.hc360bak");
        await using (var output = File.Create(file))
        await using (var encryptor = BackupEncryption.CreateEncryptor(output, recoveryKey))
        {
            await BackupArchive.WriteAsync(encryptor, manifest, protector.ExportMasterKey(), ring.Keys, dump, machine.Files, documents, CancellationToken.None);
            await encryptor.CompleteAsync(CancellationToken.None);
        }

        return (file, recoveryKey);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
