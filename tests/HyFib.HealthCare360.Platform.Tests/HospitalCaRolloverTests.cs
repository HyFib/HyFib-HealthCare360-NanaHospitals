using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The hospital CA is never made again by itself, as every device trusts it (docs/deployment/CA-ROLLOVER.md): a new one is
/// made by hand, keeping the old one so it can be put back, and a server whose CA has expired refuses to start with a
/// message that says what to do, rather than failing to issue a certificate.
/// </summary>
public sealed class HospitalCaRolloverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_new_CA_takes_the_place_of_the_old_one_which_is_kept_and_can_be_put_back()
    {
        var (directories, keys) = Machine();
        var old = Start(directories, keys, DateTimeOffset.UtcNow);

        var made = LanCertificateManager.NewCa(directories, keys, "Nana Hospitals", DateTimeOffset.UtcNow);

        made.Fingerprint.ShouldNotBe(old.CaFingerprint);
        (made.PreviousFingerprint, made.PreviousExpiresAt).ShouldBe((old.CaFingerprint, old.CaExpiresAt));
        made.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddYears(10).AddDays(-2));
        File.Exists(Path.Combine(directories.Certificates, made.PreviousKeptAs.ShouldNotBeNull())).ShouldBeTrue("the old CA is kept, never deleted");
        File.Exists(Path.Combine(directories.Certificates, "server.pfx.protected")).ShouldBeFalse("its server certificate goes with it");
        using (var handedOut = LanCertificateManager.PublicCa(directories).ShouldNotBeNull())
        {
            CertificateProbe.Fingerprint(handedOut).ShouldBe(made.Fingerprint, "tablets and browsers download the new CA");
        }

        var renewed = Start(directories, keys, DateTimeOffset.UtcNow);
        renewed.CaFingerprint.ShouldBe(made.Fingerprint, "the server announces the new CA, which the devices pin when set up again");
        renewed.IssuedByItsCa.ShouldBeTrue("and serves a certificate the new CA issued");

        // Putting the old CA back, as CA-ROLLOVER.md says: the new one aside, the old one in its place, the server
        // certificate removed; the server then issues itself one from the old CA again.
        var path = Path.Combine(directories.Certificates, "hospital-ca.pfx.protected");
        File.Move(path, path + ".withdrawn");
        File.Move(Path.Combine(directories.Certificates, made.PreviousKeptAs), path);
        File.Delete(Path.Combine(directories.Certificates, "server.pfx.protected"));
        File.Delete(Path.Combine(directories.Certificates, "server.meta.json"));

        var restored = Start(directories, keys, DateTimeOffset.UtcNow);
        restored.CaFingerprint.ShouldBe(old.CaFingerprint);
        restored.IssuedByItsCa.ShouldBeTrue();
    }

    [Fact]
    public void A_server_whose_CA_has_expired_does_not_start_and_says_what_to_do()
    {
        var (directories, keys) = Machine();
        var first = Start(directories, keys, DateTimeOffset.UtcNow);
        var afterExpiry = first.CaExpiresAt!.Value.AddDays(3);

        var problem = Should.Throw<InvalidOperationException>(() => Start(directories, keys, afterExpiry));

        problem.Message.ShouldContain($"the hospital CA expired on {first.CaExpiresAt:dd MMM yyyy}");
        problem.Message.ShouldContain("certificates new-ca");
        problem.Message.ShouldContain("CA-ROLLOVER.md");

        // Doing what it says: the server starts, with a new CA ten years from then.
        var made = LanCertificateManager.NewCa(directories, keys, "Nana Hospitals", afterExpiry);
        var started = Start(directories, keys, afterExpiry);
        started.CaFingerprint.ShouldBe(made.Fingerprint);
        started.CaExpiresAt.ShouldBe(made.ExpiresAt);
    }

    [Fact]
    public void A_running_server_whose_CA_expires_keeps_its_certificate_when_the_network_changes()
    {
        var (directories, keys) = Machine();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var options = new PlatformOptions { DataDirectory = directories.Root };
        using var server = new LanCertificateManager(directories, options, keys, NullLogger<LanCertificateManager>.Instance, clock);
        server.EnsureCertificates();
        var serving = server.ServerCertificate.Thumbprint;

        // A name the certificate lacks, as a new address on the network would be, once nothing can be issued from the CA.
        clock.Now = server.CaExpiresAt!.Value.AddDays(3);
        options.Network.AdditionalHostNames = ["hc360-new-name"];

        server.RefreshIfNeeded().ShouldBeFalse();
        server.ServerCertificate.Thumbprint.ShouldBe(serving);
    }

    /// <summary>A facility's name may hold a comma, as "Sri Ramachandra Hospital, Porur" does: the CA's name escapes it.</summary>
    [Fact]
    public void A_facility_whose_name_has_a_comma_gets_its_CA_named_after_it_and_the_day()
    {
        var (directories, keys) = Machine();
        var options = new PlatformOptions { DataDirectory = directories.Root, DefaultFacilityName = "Sri Ramachandra Hospital, Porur" };
        using var server = new LanCertificateManager(directories, options, keys, NullLogger<LanCertificateManager>.Instance);

        server.EnsureCertificates();

        using var ca = LanCertificateManager.PublicCa(directories).ShouldNotBeNull();
        ca.SubjectName.EnumerateRelativeDistinguishedNames().Select(n => n.GetSingleElementValue()!).ShouldBe(
            ["Sri Ramachandra Hospital, Porur", "HyFib HealthCare360", $"HyFib HealthCare360 Hospital CA ({Environment.MachineName} {DateTime.UtcNow:yyyy-MM-dd})"],
            ignoreOrder: true);
    }

    private (DataDirectories Directories, IDataProtectionProvider Keys) Machine() =>
        (new DataDirectories(new PlatformOptions { DataDirectory = _root }), new EphemeralDataProtectionProvider());

    /// <summary>Starts the server's TLS as HC360 does at <paramref name="now"/>, and says what it serves.</summary>
    private static Started Start(DataDirectories directories, IDataProtectionProvider keys, DateTimeOffset now)
    {
        using var certificates = new LanCertificateManager(
            directories, new PlatformOptions { DataDirectory = directories.Root }, keys, NullLogger<LanCertificateManager>.Instance, new Clock(now));
        certificates.EnsureCertificates();
        using var ca = X509Certificate2.CreateFromPem(certificates.CaPem!);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        var issuedByItsCa = chain.Build(certificates.ServerCertificate) && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData);
        return new Started(certificates.CaFingerprint!, certificates.CaExpiresAt, issuedByItsCa);
    }

    private sealed record Started(string CaFingerprint, DateTimeOffset? CaExpiresAt, bool IssuedByItsCa);

    /// <summary>A clock the test sets; the server's own expiry check is left to its timer, which a test does not wait for.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
