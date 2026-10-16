using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS SCAL-01: several servers behind a reverse proxy share one set of keys in a shared folder, protected by the
/// cluster's certificate instead of one machine's DPAPI. What one node encrypts or signs, every node opens; a key rotated
/// on one node is used and accepted by the others at once; and a node with keys of its own is not allowed to start on a
/// shared folder that has none, which would make new keys and leave everything encrypted before unreadable.
/// </summary>
public sealed class SharedKeyFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly string _certificate;

    public SharedKeyFolderTests()
    {
        Directory.CreateDirectory(_root);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=HC360 cluster keys (test)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _certificate = Path.Combine(_root, "cluster-keys.pfx");
        File.WriteAllBytes(_certificate, certificate.Export(X509ContentType.Pfx, "test-password"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_value_encrypted_on_one_node_is_read_on_another()
    {
        var (a, b) = (Protector(Node("a")), Protector(Node("b")));

        var encrypted = a.Protect("1234-5678-9012");

        b.Unprotect(encrypted).ShouldBe("1234-5678-9012");
        (a.CurrentKeyId, a.Fingerprint).ShouldBe((b.CurrentKeyId, b.Fingerprint));
        b.BlindIndex("123456789012").ShouldBe(a.BlindIndex("123456789012"));
    }

    [Fact]
    public void The_shared_key_ring_is_encrypted_with_the_cluster_certificate()
    {
        var node = Node("a");
        Protector(node).Protect("x");

        var ring = Directory.GetFiles(Path.Combine(node.Keys, "dataprotection"), "key-*.xml").ShouldHaveSingleItem();
        var xml = File.ReadAllText(ring);
        xml.ShouldContain("EncryptedXmlDecryptor");
        xml.ShouldNotContain("<masterKey");
    }

    [Fact]
    public void A_data_key_rotated_on_one_node_is_used_and_read_by_the_others()
    {
        var (a, b) = (Protector(Node("a")), Protector(Node("b")));
        var original = b.CurrentKeyId;

        var rotated = a.Rotate();
        var encrypted = a.Protect("ABHA 91-2345-6789-0123");

        // B has not looked at the folder since: the value names a key it does not have, so it reads the ring again.
        b.Unprotect(encrypted).ShouldBe("ABHA 91-2345-6789-0123");
        b.Refresh();
        b.CurrentKeyId.ShouldBe(rotated);
        b.Keys.Select(k => k.Id).ShouldBe([original, rotated], ignoreOrder: true);
        b.Protect("x").ShouldStartWith($"v2.{rotated}.");
    }

    [Fact]
    public void A_retired_key_is_gone_from_every_node()
    {
        var (a, b) = (Protector(Node("a")), Protector(Node("b")));
        var first = a.Rotate();
        a.Rotate();
        b.Refresh();
        b.Keys.Select(k => k.Id).ShouldContain(first);

        a.Retire(first);
        b.Refresh();

        b.Keys.Select(k => k.Id).ShouldNotContain(first);
    }

    [Fact]
    public void A_signing_key_rotated_on_one_node_validates_on_the_others_at_once()
    {
        var clock = TimeProvider.System;
        var options = Options.Create(new PlatformOptions { Security = { AccessTokenMinutes = 10 } });
        var (nodeA, nodeB) = (Node("a"), Node("b"));
        var a = new SigningKeyStore(nodeA, HospitalDataProtection.CreateProvider(nodeA), options, clock);
        var b = new SigningKeyStore(nodeB, HospitalDataProtection.CreateProvider(nodeB), options, clock);
        var before = b.Key.KeyId;
        a.Key.KeyId.ShouldBe(before);

        var after = a.Rotate();

        // A token signed on A with the new key names it, and B accepts it: B reads the key files again for a key it does
        // not know, and keeps accepting tokens signed before the rotation for as long as they live.
        b.ValidationKeys(after).Select(k => k.KeyId).ShouldBe([after, before]);
        b.Key.KeyId.ShouldBe(after);
    }

    [Fact]
    public void A_node_with_keys_of_its_own_may_not_start_on_a_shared_folder_without_them()
    {
        var local = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, "c") });
        local.EnsureCreated();
        new FieldProtector(local, HospitalDataProtection.CreateProvider(local)).Protect("made before the cluster");

        var problem = FieldMasterKey.SharedFolderProblem(Node("c"));

        problem.ShouldNotBeNull().ShouldContain("backup restore");
        FieldMasterKey.SharedFolderProblem(local).ShouldBeNull();
    }

    [Fact]
    public void A_node_joining_a_shared_folder_that_has_the_keys_may_start()
    {
        Protector(Node("a")).Protect("x");
        var local = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, "b") });
        local.EnsureCreated();
        new FieldProtector(local, HospitalDataProtection.CreateProvider(local)).Protect("its own, from before");

        FieldMasterKey.SharedFolderProblem(Node("b")).ShouldBeNull();
    }

    [Fact]
    public void A_configured_certificate_that_cannot_be_found_stops_the_node()
    {
        var cluster = new ClusterOptions { SharedDirectory = Path.Combine(_root, "shared"), KeyCertificatePath = Path.Combine(_root, "missing.pfx") };

        Record.Exception(() => ClusterKeyCertificate.Load(cluster)).ShouldNotBeNull();
        ClusterKeyCertificate.Load(new ClusterOptions()).ShouldBeNull();
    }

    private static FieldProtector Protector(DataDirectories node) => new(node, HospitalDataProtection.CreateProvider(node));

    /// <summary>One server: a data directory of its own, and the shared folder every node uses for keys and documents.</summary>
    private DataDirectories Node(string name)
    {
        var node = new DataDirectories(new PlatformOptions
        {
            DataDirectory = Path.Combine(_root, name),
            Cluster =
            {
                SharedDirectory = Path.Combine(_root, "shared"),
                KeyCertificatePath = _certificate,
                KeyCertificatePassword = "test-password",
            },
        });
        node.EnsureCreated();
        return node;
    }
}
