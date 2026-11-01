using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Trust;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// HC360 Desktop and the lab agent take a new hospital CA only on the word of the CA they pin
/// (<see cref="HospitalCaSuccession"/>): checked here against the server's own rollover ('certificates roll-ca'), and against
/// what someone who knows the CA's name, which is public, could make.
/// </summary>
public sealed class HospitalCaSuccessionTests : IDisposable
{
    /// <summary>When the old CA was made: nine and a half years ago, so it is in its last year, as a CA is when it is rolled over.</summary>
    private static readonly DateTimeOffset Made = DateTimeOffset.UtcNow.AddYears(-9).AddMonths(-6);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_CA_a_rollover_makes_succeeds_the_one_devices_pin_on_the_old_CAs_word_and_only_while_it_holds()
    {
        var now = DateTimeOffset.UtcNow;
        var (oldCa, newCa, sent, rolled) = Rolled("primary", now);
        using (oldCa)
        using (newCa)
        {
            HospitalCaSuccession.Succeeds(oldCa, newCa, sent, now).ShouldBeTrue("the old CA signed a certificate for the new key, and the server sent it");
            HospitalCaSuccession.Succeeds(oldCa, newCa, [], now).ShouldBeFalse("without the old CA's word");
            HospitalCaSuccession.Succeeds(newCa, oldCa, sent, now).ShouldBeFalse("not backwards");
            HospitalCaSuccession.Succeeds(oldCa, oldCa, sent, now).ShouldBeFalse("the same CA is not its own successor");
            HospitalCaSuccession.Succeeds(oldCa, newCa, sent, rolled.CrossSignedUntil.AddDays(1)).ShouldBeFalse("an expired CA vouches for nothing");
        }
    }

    /// <summary>
    /// What a device keeps after it followed: the certificate the old CA signed shows the succession for good, after it
    /// expired too, which is how an IT policy that still names the old CA keeps accepting the new one. Here the rollover was a
    /// year ago and the old CA expired half a year ago.
    /// </summary>
    [Fact]
    public void The_certificate_the_old_CA_signed_shows_the_succession_after_it_expired_and_only_for_its_own_pair()
    {
        var rolledAt = DateTimeOffset.UtcNow.AddYears(-1);
        var (directories, keys) = Machine("primary");
        string oldPem;
        using (var first = Start(directories, keys, rolledAt.AddYears(-9).AddMonths(-6)))
        {
            oldPem = first.CaPem!;
        }

        LanCertificateManager.RollCa(directories, keys, rolledAt);
        string newPem;
        using (var rolled = Start(directories, keys, rolledAt))
        {
            newPem = rolled.CaPem!;
        }

        using var oldCa = X509Certificate2.CreateFromPem(oldPem);
        using var newCa = X509Certificate2.CreateFromPem(newPem);
        using var cross = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directories.Certificates, "hospital-ca-cross.crt")));
        var (otherOld, otherNew, otherSent, _) = Rolled("other-hospital", DateTimeOffset.UtcNow);
        using (otherOld)
        using (otherNew)
        {
            using var otherCross = HospitalCaSuccession.CrossFor(otherOld, otherNew, otherSent, DateTimeOffset.UtcNow).ShouldNotBeNull();

            HospitalCaSuccession.Succeeds(oldCa, newCa, [cross], rolledAt).ShouldBeTrue("as a device followed it then");
            HospitalCaSuccession.Succeeds(oldCa, newCa, [cross], DateTimeOffset.UtcNow).ShouldBeFalse("nothing to follow once it expired");
            HospitalCaSuccession.Succeeded(oldCa, newCa, cross).ShouldBeTrue("but it still shows the new CA succeeded the old one");
            HospitalCaSuccession.Succeeded(newCa, oldCa, cross).ShouldBeFalse("not backwards");
            HospitalCaSuccession.Succeeded(otherOld, newCa, cross).ShouldBeFalse("another CA did not sign it");
            HospitalCaSuccession.Succeeded(oldCa, newCa, otherCross).ShouldBeFalse("another hospital's rollover");
        }
    }

    /// <summary>The CA's name is public (on every certificate the server serves): a CA made in its name proves nothing.</summary>
    [Fact]
    public void A_CA_made_in_the_hospital_CAs_name_with_a_key_of_its_own_is_refused_whatever_comes_with_it()
    {
        var now = DateTimeOffset.UtcNow;
        var (pinned, realNew, realSent, _) = Rolled("primary", now);
        var (otherOld, otherNew, otherSent, _) = Rolled("other-hospital", now);
        using (pinned)
        using (realNew)
        using (otherOld)
        using (otherNew)
        {
            using var forgerKey = RSA.Create(2048);
            using var forged = Ca(pinned.SubjectName, forgerKey, issuer: null, now);
            using var vouchOfItsOwn = Ca(pinned.SubjectName, forgerKey, issuer: forged, now);

            HospitalCaSuccession.Succeeds(pinned, forged, [vouchOfItsOwn], now).ShouldBeFalse("it vouches for itself");
            HospitalCaSuccession.Succeeds(pinned, forged, realSent, now).ShouldBeFalse("the old CA vouched for another key");
            HospitalCaSuccession.Succeeds(pinned, forged, [.. realSent, .. otherSent], now).ShouldBeFalse("another hospital's CA vouched for its own");

            // Another hospital's real rollover. Its CA may even have this one's name (a server of the same name, set up the same
            // day, as both are here), but its own old CA vouched for it, not the one pinned.
            HospitalCaSuccession.Succeeds(pinned, otherNew, otherSent, now).ShouldBeFalse("another hospital's rollover");
            HospitalCaSuccession.Succeeds(pinned, otherNew, [.. otherSent, .. realSent], now).ShouldBeFalse("the pinned CA vouched for another key");
        }
    }

    /// <summary>
    /// A successor is what a rollover makes and nothing else: a CA in the old one's name, its own issuer, and the old CA's word
    /// for it is a CA certificate in that name for its key. The old CA signs nothing else of the kind, so each rule is checked
    /// here on certificates made by hand.
    /// </summary>
    [Fact]
    public void Only_a_CA_in_the_old_CAs_name_that_issued_itself_and_a_CA_certificate_for_it_make_a_successor()
    {
        var now = DateTimeOffset.UtcNow;
        var name = new X500DistinguishedName("CN=Test Hospital CA, O=HyFib HealthCare360");
        var otherName = new X500DistinguishedName("CN=Some other CA, O=HyFib HealthCare360");
        using var oldKey = RSA.Create(2048);
        using var newKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        using var pinned = Ca(name, oldKey, issuer: null, now);
        using var next = Ca(name, newKey, issuer: null, now);
        using var vouch = Ca(name, newKey, issuer: pinned, now);

        HospitalCaSuccession.Succeeds(pinned, next, [vouch], now).ShouldBeTrue("as a rollover makes it");

        using var notACa = Leaf(name, next, pinned, now);
        HospitalCaSuccession.Succeeds(pinned, next, [notACa], now).ShouldBeFalse("the old CA's word is not a CA certificate");

        using var inAnotherName = Ca(otherName, newKey, issuer: pinned, now);
        HospitalCaSuccession.Succeeds(pinned, next, [inAnotherName], now).ShouldBeFalse("the old CA's word names another CA");

        using var otherCa = Ca(otherName, otherKey, issuer: null, now);
        using var nextInAnotherName = Ca(otherName, newKey, issuer: null, now);
        using var vouchInAnotherName = Ca(otherName, newKey, issuer: pinned, now);
        HospitalCaSuccession.Succeeds(pinned, nextInAnotherName, [vouchInAnotherName], now).ShouldBeFalse("a CA in another name");

        using var issuedByAnother = Ca(name, newKey, issuer: otherCa, now);
        HospitalCaSuccession.Succeeds(pinned, issuedByAnother, [vouch], now).ShouldBeFalse("a CA another CA issued is not one of its own");

        using var nextNotACa = Leaf(name, next, next, now);
        HospitalCaSuccession.Succeeds(pinned, nextNotACa, [vouch], now).ShouldBeFalse("not a CA");
    }

    /// <summary>
    /// A server rolled over at <paramref name="now"/> from a CA made nine and a half years earlier: the old CA, the new one,
    /// what the server sends after its own certificate, and the rollover.
    /// </summary>
    private (X509Certificate2 Old, X509Certificate2 New, X509Certificate2[] Sent, RolledHospitalCa Rolled) Rolled(string machine, DateTimeOffset now)
    {
        var (directories, keys) = Machine(machine);
        string oldPem;
        using (var first = Start(directories, keys, Made))
        {
            oldPem = first.CaPem!;
        }

        var rolled = LanCertificateManager.RollCa(directories, keys, now);
        using var server = Start(directories, keys, now);
        var sent = server.ServerCertificateContext!.IntermediateCertificates.Select(c => X509CertificateLoader.LoadCertificate(c.RawData)).ToArray();
        return (X509Certificate2.CreateFromPem(oldPem), X509Certificate2.CreateFromPem(server.CaPem!), sent, rolled);
    }

    private (DataDirectories Directories, IDataProtectionProvider Keys) Machine(string name) =>
        (new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, name) }), new EphemeralDataProtectionProvider());

    private static LanCertificateManager Start(DataDirectories directories, IDataProtectionProvider keys, DateTimeOffset now)
    {
        var certificates = new LanCertificateManager(
            directories, new PlatformOptions { DataDirectory = directories.Root }, keys, NullLogger<LanCertificateManager>.Instance, new Clock(now));
        certificates.EnsureCertificates();
        return certificates;
    }

    /// <summary>A CA named <paramref name="name"/> for <paramref name="key"/>: self-signed, or signed by <paramref name="issuer"/>.</summary>
    private static X509Certificate2 Ca(X500DistinguishedName name, RSA key, X509Certificate2? issuer, DateTimeOffset now)
    {
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (issuer is null)
        {
            return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        }

        // As every certificate HC360 issues: the issuer's key named, so a chain is built through the issuer and not mistaken for
        // a self-signed certificate (a self-issued one without it is taken for one).
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        return request.Create(issuer, now.AddDays(-1), now.AddYears(5), RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>
    /// A certificate that is not a CA, named <paramref name="name"/>, for <paramref name="keyOf"/>'s key, signed by
    /// <paramref name="issuer"/>. It names its own key as well as its issuer's, as a CA's would: without its own, a certificate
    /// issued in its own name is taken for self-signed, and the chain to its issuer is never built, whatever it is.
    /// </summary>
    private static X509Certificate2 Leaf(X500DistinguishedName name, X509Certificate2 keyOf, X509Certificate2 issuer, DateTimeOffset now)
    {
        var request = new CertificateRequest(name, keyOf.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        return request.Create(issuer, now.AddDays(-1), now.AddYears(1), RandomNumberGenerator.GetBytes(16));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
