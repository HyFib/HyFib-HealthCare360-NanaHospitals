using System.Formats.Tar;
using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.WindowsInterop.Trust;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The hospital CA travels with the backups, inside their encryption, so the server a backup is restored onto (replacement
/// hardware after a disaster) serves a certificate every device already trusts, and none is set up again. A server whose CA
/// took the backup's CA's place since the backup was made (a rollover, or a new CA for a key that may have been seen)
/// keeps its own, which the devices trust now.
/// </summary>
public sealed class HospitalCaBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_backup_restored_on_new_hardware_installs_the_hospital_CA_and_the_devices_trust_the_new_server()
    {
        var primary = Node("primary");
        var pinned = primary.Ca(DateTimeOffset.UtcNow);
        var (backup, recoveryKey) = await BackupAsync(primary);

        // The new hardware's HC360 started once before the restore, and made a CA of its own.
        var replacement = Node("replacement");
        var own = replacement.Ca(DateTimeOffset.UtcNow);

        var result = await BackupRestore.RestoreAsync(backup, recoveryKey, null, replacement.Directories, replacement.Keys, replacement.Keys, TimeProvider.System, Ct);

        result.Manifest.ShouldNotBeNull().HospitalCa.ShouldBe(pinned.Fingerprint);
        var ca = result.HospitalCa.ShouldNotBeNull();
        (ca.Fingerprint, ca.Installed, ca.KeptFingerprint).ShouldBe((pinned.Fingerprint, true, null));
        var keptAs = ca.PreviousKeptAs.ShouldNotBeNull();
        File.Exists(Path.Combine(replacement.Directories.Certificates, keptAs)).ShouldBeTrue($"the CA it made itself ({own.Fingerprint}) is kept, never deleted");

        using var restored = replacement.Start(DateTimeOffset.UtcNow);
        restored.CaFingerprint.ShouldBe(pinned.Fingerprint, "it announces the CA the devices pinned");
        await using var app = await HospitalCaTestServer.ServeAsync(restored);
        using var device = new HospitalCaPins(pinned.Pem);
        (await HospitalCaTestServer.TrustsAsync(app, device)).ShouldBeTrue("a device set up with the old server trusts the new one as it is");
    }

    [Fact]
    public async Task A_server_switched_to_the_next_CA_since_the_backup_keeps_it()
    {
        var node = Node("primary");
        var made = DateTimeOffset.UtcNow.AddYears(-9).AddMonths(-6);
        var before = node.Ca(made);
        var (backup, recoveryKey) = await BackupAsync(node);
        var now = DateTimeOffset.UtcNow;
        LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        var switched = LanCertificateManager.SwitchCa(node.Directories, node.Keys, now);

        var result = await BackupRestore.RestoreAsync(backup, recoveryKey, null, node.Directories, node.Keys, node.Keys, TimeProvider.System, Ct);

        var ca = result.HospitalCa.ShouldNotBeNull();
        (ca.Fingerprint, ca.Installed, ca.KeptFingerprint).ShouldBe((before.Fingerprint, false, switched.Fingerprint));
        using var server = node.Start(now);
        (server.CaFingerprint, server.PreviousCaFingerprint).ShouldBe((switched.Fingerprint, before.Fingerprint), "the devices switched to it, and the one it replaced is still handed out");
    }

    [Fact]
    public async Task A_server_given_a_new_CA_since_the_backup_keeps_it_and_never_takes_back_one_whose_key_may_have_been_seen()
    {
        var node = Node("primary");
        var exposed = node.Ca(DateTimeOffset.UtcNow);
        var (backup, recoveryKey) = await BackupAsync(node);
        var made = LanCertificateManager.NewCa(node.Directories, node.Keys, "Nana Hospitals", DateTimeOffset.UtcNow);

        var result = await BackupRestore.RestoreAsync(backup, recoveryKey, null, node.Directories, node.Keys, node.Keys, TimeProvider.System, Ct);

        var ca = result.HospitalCa.ShouldNotBeNull();
        (ca.Fingerprint, ca.Installed, ca.KeptFingerprint).ShouldBe((exposed.Fingerprint, false, made.Fingerprint));
        using var server = node.Start(DateTimeOffset.UtcNow);
        server.CaFingerprint.ShouldBe(made.Fingerprint, "every device was set up again with the new CA");
    }

    [Fact]
    public async Task The_same_CA_restored_changes_nothing_and_a_server_without_a_CA_backs_up_none()
    {
        var node = Node("primary");
        var pinned = node.Ca(DateTimeOffset.UtcNow);
        var (backup, recoveryKey) = await BackupAsync(node);

        var same = await BackupRestore.RestoreAsync(backup, recoveryKey, null, node.Directories, node.Keys, node.Keys, TimeProvider.System, Ct);

        var sameCa = same.HospitalCa.ShouldNotBeNull();
        sameCa.Installed.ShouldBeFalse();
        sameCa.PreviousKeptAs.ShouldBeNull();
        Directory.GetFiles(node.Directories.Certificates, "hospital-ca.pfx.protected.replaced-*").ShouldBeEmpty();
        using (var server = node.Start(DateTimeOffset.UtcNow))
        {
            server.CaFingerprint.ShouldBe(pinned.Fingerprint);
        }

        var unstarted = Node("unstarted");
        var (none, noneKey) = await BackupAsync(unstarted);
        var target = Node("target");
        var result = await BackupRestore.RestoreAsync(none, noneKey, null, target.Directories, target.Keys, target.Keys, TimeProvider.System, Ct);
        result.Manifest.ShouldNotBeNull().HospitalCa.ShouldBeNull("a server before its first start has no CA to carry");
        result.HospitalCa.ShouldBeNull();
    }

    [Fact]
    public async Task An_archive_whose_hospital_CA_is_not_the_one_its_manifest_names_or_is_missing_is_refused()
    {
        var node = Node("primary");
        node.Ca(DateTimeOffset.UtcNow);
        using var certificates = node.Start(DateTimeOffset.UtcNow);
        var exported = certificates.ExportForBackup().ShouldNotBeNull();
        var other = Node("other");
        other.Ca(DateTimeOffset.UtcNow);
        using var otherCertificates = other.Start(DateTimeOffset.UtcNow);
        var otherExported = otherCertificates.ExportForBackup().ShouldNotBeNull();
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var dump = Path.Combine(_root, "database.dump");
        File.WriteAllBytes(dump, "PGDMP"u8.ToArray());

        var swapped = new MemoryStream();
        await BackupArchive.WriteIndexedAsync(swapped, Manifest(masterKey), masterKey, [], null, dump, [], new BackupExtras(HospitalCa: exported with { Pkcs12 = otherExported.Pkcs12 }), Ct);
        swapped.Position = 0;
        (await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(swapped, new BackupReadTargets(), Ct))).Message
            .ShouldContain("hospital CA in the backup does not match its manifest");

        var written = new MemoryStream();
        await BackupArchive.WriteIndexedAsync(written, Manifest(masterKey), masterKey, [], null, dump, [], new BackupExtras(HospitalCa: exported), Ct);
        written.Position = 0;
        var contents = await BackupArchive.ReadAsync(written, new BackupReadTargets(), Ct);
        contents.Manifest.HospitalCa.ShouldBe(exported.Fingerprint);
        contents.HospitalCa.ShouldBe(exported.Pkcs12);

        var lost = new MemoryStream();
        await using (var tar = new TarWriter(lost, TarEntryFormat.Pax, leaveOpen: true))
        {
            await using var archive = new MemoryStream(written.ToArray());
            await using var reader = new TarReader(archive);
            while (await reader.GetNextEntryAsync(copyData: true, Ct) is { } entry)
            {
                if (entry.Name != BackupArchive.HospitalCaEntry)
                {
                    await tar.WriteEntryAsync(entry, Ct);
                }
            }
        }

        lost.Position = 0;
        (await Should.ThrowAsync<InvalidDataException>(() => BackupArchive.ReadAsync(lost, new BackupReadTargets(), Ct))).Message
            .ShouldContain("hospital CA its manifest lists is missing");
    }

    private HospitalCaTestServer Node(string name) => new(_root, name);

    /// <summary>A backup of <paramref name="node"/> as the backup service writes it, with the CA its TLS holds, if any.</summary>
    private async Task<(string File, byte[] RecoveryKey)> BackupAsync(HospitalCaTestServer node)
    {
        node.Directories.EnsureCreated();
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var recoveryKey = BackupRecoveryKey.Original(masterKey);
        var dump = Path.Combine(node.Directories.Temp, "database.dump");
        File.WriteAllBytes(dump, [.. "PGDMP"u8, .. RandomNumberGenerator.GetBytes(64)]);
        ExportedHospitalCa? ca = null;
        if (LanCertificateManager.PublicCa(node.Directories) is { } started)
        {
            started.Dispose();
            using var certificates = node.Start(DateTimeOffset.UtcNow);
            ca = certificates.ExportForBackup();
        }

        var file = Path.Combine(node.Directories.Backups, $"{Guid.NewGuid():N}.hc360bak");
        await using (var output = File.Create(file))
        await using (var encryptor = BackupEncryption.CreateEncryptor(output, recoveryKey))
        {
            await BackupArchive.WriteIndexedAsync(encryptor, Manifest(masterKey), masterKey, [], null, dump, [], new BackupExtras(HospitalCa: ca), Ct);
            await encryptor.CompleteAsync(Ct);
        }

        return (file, recoveryKey);
    }

    private static BackupManifest Manifest(byte[] masterKey) =>
        new(BackupArchive.Format, "Manual", DateTimeOffset.UtcNow, "1.0.0", "hc360", [], true, 0, 0, FieldMasterKey.Fingerprint(masterKey));
}
