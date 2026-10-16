using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-20: the hospital CA moves to a warm standby (or replacement hardware) in a password-protected file, so the
/// server there issues certificates that workstations, which pin the CA, already trust. Each server keeps it protected for
/// its own machine, as it keeps the CA it made itself.
/// </summary>
public sealed class HospitalCaTransferTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_standby_that_imports_the_CA_serves_a_certificate_workstations_already_trust()
    {
        var (primary, primaryKeys) = Machine("primary");
        var primaryFingerprint = Start(primary, primaryKeys, out var primaryCaPem);
        var exported = LanCertificateManager.ExportCa(primary, primaryKeys, Password);
        exported.Fingerprint.ShouldBe(primaryFingerprint);

        // The standby's HC360 had started once, and made a CA of its own.
        var (standby, standbyKeys) = Machine("standby");
        var ownFingerprint = Start(standby, standbyKeys, out _);
        ownFingerprint.ShouldNotBe(primaryFingerprint);

        var imported = LanCertificateManager.ImportCa(standby, standbyKeys, exported.Pkcs12, Password, DateTimeOffset.UtcNow);

        (imported.Fingerprint, imported.Changed).ShouldBe((primaryFingerprint, true));
        imported.PreviousKeptAs.ShouldNotBeNull().ShouldStartWith("hospital-ca.pfx.protected.replaced-");
        File.Exists(Path.Combine(standby.Certificates, imported.PreviousKeptAs)).ShouldBeTrue("the standby's own CA is kept, never deleted");
        File.ReadAllText(Path.Combine(standby.Certificates, "hc360-hospital-ca.crt")).ShouldBe(primaryCaPem, "browsers and tablets download the same CA");

        using var server = new LanCertificateManager(standby, new PlatformOptions { DataDirectory = standby.Root }, standbyKeys, NullLogger<LanCertificateManager>.Instance);
        server.EnsureCertificates();
        server.CaFingerprint.ShouldBe(primaryFingerprint, "the standby announces the CA workstations pinned");
        server.CaExpiresAt.ShouldBe(imported.ExpiresAt, "System health shows when the imported CA expires");
        ChainsTo(server.ServerCertificate, primaryCaPem).ShouldBeTrue("its server certificate is issued by the hospital CA");
    }

    [Fact]
    public void The_CA_is_kept_for_the_standby_machine_alone()
    {
        var (primary, primaryKeys) = Machine("primary");
        Start(primary, primaryKeys, out _);
        var exported = LanCertificateManager.ExportCa(primary, primaryKeys, Password);
        var (standby, standbyKeys) = Machine("standby");

        LanCertificateManager.ImportCa(standby, standbyKeys, exported.Pkcs12, Password, DateTimeOffset.UtcNow);

        // A copy of the standby's file opens only with the standby's keys, as the primary's own opens only with the primary's.
        Should.Throw<CryptographicException>(() =>
            LanCertificateManager.ExportCa(standby, new EphemeralDataProtectionProvider(), Password));
        LanCertificateManager.ExportCa(standby, standbyKeys, Password).Fingerprint.ShouldBe(exported.Fingerprint);
    }

    [Fact]
    public void A_server_certificate_issued_by_another_CA_is_issued_again()
    {
        var (primary, primaryKeys) = Machine("primary");
        Start(primary, primaryKeys, out var primaryCaPem);
        var exported = LanCertificateManager.ExportCa(primary, primaryKeys, Password);
        var (standby, standbyKeys) = Machine("standby");
        Start(standby, standbyKeys, out _);
        var serverFiles = new[] { "server.pfx.protected", "server.meta.json" }
            .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(standby.Certificates, name)));

        LanCertificateManager.ImportCa(standby, standbyKeys, exported.Pkcs12, Password, DateTimeOffset.UtcNow);
        // As a server that was still running would write its certificate back, issued by the CA it had in memory.
        foreach (var (name, bytes) in serverFiles)
        {
            File.WriteAllBytes(Path.Combine(standby.Certificates, name), bytes);
        }

        using var server = new LanCertificateManager(standby, new PlatformOptions { DataDirectory = standby.Root }, standbyKeys, NullLogger<LanCertificateManager>.Instance);
        server.EnsureCertificates();
        ChainsTo(server.ServerCertificate, primaryCaPem).ShouldBeTrue();
    }

    [Fact]
    public void Importing_the_CA_a_server_already_has_changes_nothing()
    {
        var (primary, primaryKeys) = Machine("primary");
        Start(primary, primaryKeys, out _);
        var exported = LanCertificateManager.ExportCa(primary, primaryKeys, Password);
        var serverCertificate = File.ReadAllBytes(Path.Combine(primary.Certificates, "server.pfx.protected"));

        var imported = LanCertificateManager.ImportCa(primary, primaryKeys, exported.Pkcs12, Password, DateTimeOffset.UtcNow);

        (imported.Changed, imported.PreviousKeptAs).ShouldBe((false, null));
        File.ReadAllBytes(Path.Combine(primary.Certificates, "server.pfx.protected")).ShouldBe(serverCertificate);
    }

    [Fact]
    public void The_file_opens_only_with_its_password_and_needs_a_long_one()
    {
        var (primary, primaryKeys) = Machine("primary");
        Start(primary, primaryKeys, out _);
        var exported = LanCertificateManager.ExportCa(primary, primaryKeys, Password);
        var (standby, standbyKeys) = Machine("standby");

        Should.Throw<CryptographicException>(() =>
            LanCertificateManager.ImportCa(standby, standbyKeys, exported.Pkcs12, "not the password at all", DateTimeOffset.UtcNow));
        Directory.Exists(standby.Certificates).ShouldBeFalse("nothing was written");
        Should.Throw<ArgumentException>(() => LanCertificateManager.ExportCa(primary, primaryKeys, "too short"))
            .Message.ShouldContain($"at least {LanCertificateManager.MinimumExportPasswordLength} characters");
    }

    [Fact]
    public void Only_a_CA_with_its_private_key_that_has_not_expired_is_imported()
    {
        var (standby, standbyKeys) = Machine("standby");

        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=hc360-srv", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            using var notCa = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            Should.Throw<InvalidDataException>(() =>
                    LanCertificateManager.ImportCa(standby, standbyKeys, notCa.Export(X509ContentType.Pkcs12, Password), Password, DateTimeOffset.UtcNow))
                .Message.ShouldContain("not a hospital CA");
        }

        var (primary, primaryKeys) = Machine("primary");
        Start(primary, primaryKeys, out var caPem);
        using (var publicOnly = X509Certificate2.CreateFromPem(caPem))
        {
            Should.Throw<InvalidDataException>(() =>
                    LanCertificateManager.ImportCa(standby, standbyKeys, publicOnly.Export(X509ContentType.Pkcs12, Password), Password, DateTimeOffset.UtcNow))
                .Message.ShouldContain("without its private key");
        }

        var exported = LanCertificateManager.ExportCa(primary, primaryKeys, Password);
        Should.Throw<InvalidDataException>(() =>
                LanCertificateManager.ImportCa(standby, standbyKeys, exported.Pkcs12, Password, exported.ExpiresAt.AddDays(1)))
            .Message.ShouldContain("expired");
    }

    [Fact]
    public void There_is_no_CA_to_export_before_the_server_first_starts()
    {
        var (primary, primaryKeys) = Machine("primary");

        Should.Throw<FileNotFoundException>(() => LanCertificateManager.ExportCa(primary, primaryKeys, Password));
    }

    /// <summary>A Server Node's data folder, and its Data Protection keys: another machine's do not open its files.</summary>
    private (DataDirectories Directories, IDataProtectionProvider Keys) Machine(string name) =>
        (new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, name) }), new EphemeralDataProtectionProvider());

    /// <summary>Starts the server's TLS as HC360 does, and returns its CA's fingerprint.</summary>
    private static string Start(DataDirectories directories, IDataProtectionProvider keys, out string caPem)
    {
        using var certificates = new LanCertificateManager(directories, new PlatformOptions { DataDirectory = directories.Root }, keys, NullLogger<LanCertificateManager>.Instance);
        certificates.EnsureCertificates();
        caPem = certificates.CaPem.ShouldNotBeNull();
        return certificates.CaFingerprint.ShouldNotBeNull();
    }

    /// <summary>Whether a workstation that pinned <paramref name="caPem"/> trusts <paramref name="server"/>.</summary>
    private static bool ChainsTo(X509Certificate2 server, string caPem)
    {
        using var ca = X509Certificate2.CreateFromPem(caPem);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(server) && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData);
    }
}
