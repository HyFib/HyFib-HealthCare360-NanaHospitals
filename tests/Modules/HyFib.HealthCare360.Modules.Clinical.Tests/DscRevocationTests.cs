using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// A DSC's revocation at signing (ERX-04): the CRL is read and believed only when its CA signed it; a revoked or expired DSC
/// is refused; the CA is asked when it answers (OCSP), and its cached CRL decides when it does not, with its age recorded.
/// </summary>
public sealed class DscRevocationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc360-crl-" + Guid.NewGuid().ToString("N"));
    private readonly TestPki _pki = new();

    public void Dispose()
    {
        _pki.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_crl_is_read_from_der_or_pem_and_believed_only_from_the_ca_that_signed_it()
    {
        using var doctor = _pki.Doctor();
        using var other = _pki.Doctor("Dr Ravi Kumar");
        var der = Crl(_pki.Ca, DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddDays(7), doctor);

        var list = CertificateRevocationList.TryRead(der).ShouldNotBeNull();
        list.IsSignedBy(_pki.Ca).ShouldBeTrue();
        list.IsSignedBy(_pki.Root).ShouldBeFalse("the root did not sign it");
        using var stranger = new TestPki("Another Root");
        list.IsSignedBy(stranger.Ca).ShouldBeFalse();
        list.Lists(doctor).ShouldBeTrue();
        list.Lists(other).ShouldBeFalse();
        list.IsStale(DateTimeOffset.UtcNow).ShouldBeFalse();
        list.IsStale(DateTimeOffset.UtcNow.AddDays(8)).ShouldBeTrue();

        var pem = System.Text.Encoding.ASCII.GetBytes(PemEncoding.WriteString("X509 CRL", der));
        CertificateRevocationList.TryRead(pem).ShouldNotBeNull().Lists(doctor).ShouldBeTrue();
        CertificateRevocationList.TryRead([1, 2, 3]).ShouldBeNull();
        CertificateRevocationList.TryRead(doctor.RawData).ShouldBeNull("a certificate is not a CRL");
    }

    [Fact]
    public void A_serial_with_a_leading_zero_byte_matches_the_certificate()
    {
        CertificateRevocationList.Serial([0x00, 0x8F, 0x01]).ShouldBe("8F01");
        CertificateRevocationList.Serial([0x00]).ShouldBe("00");
    }

    [Fact]
    public void The_crl_addresses_are_read_from_a_certificates_distribution_points()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dr Meera Shah, C=IN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["http://crl.example-ca.in/ca2024.crl", "ldap://x"]));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        CertificateRevocationList.DistributionPoints(certificate).ShouldBe(["http://crl.example-ca.in/ca2024.crl"]);
        using var plain = _pki.Doctor();
        CertificateRevocationList.DistributionPoints(plain).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dsc_on_the_cached_crl_is_refused_offline_and_one_not_on_it_signs_with_the_crls_age_recorded()
    {
        using var revoked = _pki.Doctor();
        using var good = _pki.Doctor("Dr Ravi Kumar");
        var cache = Cache();
        cache.Save(Crl(_pki.Ca, DateTimeOffset.UtcNow.AddHours(-30), DateTimeOffset.UtcNow.AddDays(7), revoked), "http://crl.example-ca.in/ca.crl").ShouldBeTrue();
        var check = Check(cache, OnlineRevocationAnswer.Unknown);

        var refused = await check.CheckAsync(Content, Sign(revoked), _pki.Anchors, TestContext.Current.CancellationToken);
        refused.Refusal.ShouldNotBeNull().Code.ShouldBe(DscRevocationCheck.RevokedCode);

        var accepted = await check.CheckAsync(Content, Sign(good), _pki.Anchors, TestContext.Current.CancellationToken);
        accepted.Refusal.ShouldBeNull();
        accepted.Words.ShouldStartWith("Not revoked on the CRL of");
        accepted.Words.ShouldContain("(30 hours old)");
    }

    [Fact]
    public async Task A_stale_crl_is_still_used_offline_and_says_it_is_past_its_next_update()
    {
        using var good = _pki.Doctor();
        var cache = Cache();
        cache.Save(Crl(_pki.Ca, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-3)), "http://crl.example-ca.in/ca.crl");

        var outcome = await Check(cache, OnlineRevocationAnswer.Unknown).CheckAsync(Content, Sign(good), _pki.Anchors, TestContext.Current.CancellationToken);

        outcome.Refusal.ShouldBeNull();
        outcome.Words.ShouldContain("10 days old, past its next update");
    }

    [Fact]
    public async Task The_ca_answering_revoked_refuses_even_without_a_cached_crl_and_good_is_recorded_as_online()
    {
        using var doctor = _pki.Doctor();
        var cache = Cache();

        (await Check(cache, OnlineRevocationAnswer.Revoked).CheckAsync(Content, Sign(doctor), _pki.Anchors, TestContext.Current.CancellationToken))
            .Refusal.ShouldNotBeNull().Code.ShouldBe(DscRevocationCheck.RevokedCode);
        (await Check(cache, OnlineRevocationAnswer.Good).CheckAsync(Content, Sign(doctor), _pki.Anchors, TestContext.Current.CancellationToken))
            .Words.ShouldBe("Not revoked (checked online)");
        (await Check(cache, OnlineRevocationAnswer.Unknown).CheckAsync(Content, Sign(doctor), _pki.Anchors, TestContext.Current.CancellationToken))
            .Words.ShouldStartWith("Revocation not checked");
    }

    [Fact]
    public async Task A_crl_another_ca_signed_is_not_believed()
    {
        using var doctor = _pki.Doctor();
        using var stranger = new TestPki("Another Root");
        var cache = Cache();
        cache.Save(Crl(stranger.Ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), doctor), "http://elsewhere/ca.crl");

        var outcome = await Check(cache, OnlineRevocationAnswer.Unknown).CheckAsync(Content, Sign(doctor), _pki.Anchors, TestContext.Current.CancellationToken);

        outcome.Refusal.ShouldBeNull("only the issuing CA revokes its DSCs");
        outcome.Words.ShouldStartWith("Revocation not checked");
    }

    [Fact]
    public async Task An_expired_dsc_is_refused()
    {
        using var doctor = _pki.Doctor();
        var later = new FixedTime(doctor.NotAfter.ToUniversalTime().AddDays(1));
        var check = new DscRevocationCheck(Cache(), Online(OnlineRevocationAnswer.Good), later);

        (await check.CheckAsync(Content, Sign(doctor), _pki.Anchors, TestContext.Current.CancellationToken)).Refusal.ShouldNotBeNull()
            .Code.ShouldBe(DscRevocationCheck.ExpiredCode);
    }

    [Fact]
    public void System_health_shows_the_crls_age_and_warns_of_a_stale_or_missing_one()
    {
        var now = DateTimeOffset.UtcNow;
        const string folder = @"C:\ProgramData\HyFib\HealthCare360\certs\dsc-crl";
        var fresh = new CachedCrl("CN=Test Licensed CA", now.AddHours(-5), now.AddDays(2), now, "a.crl");
        var stale = new CachedCrl("CN=Old CA", now.AddDays(-9), now.AddDays(-2), now, "b.crl");

        DscTrustHealthWarnings.CrlWarnings(PrescriptionSignaturePolicy.Off, [], folder, now).ShouldBeEmpty();
        DscTrustHealthWarnings.CrlWarnings(PrescriptionSignaturePolicy.Optional, [], folder, now).ShouldHaveSingleItem().Code
            .ShouldBe(DscTrustHealthWarnings.CrlMissingCode);
        var age = DscTrustHealthWarnings.CrlWarnings(PrescriptionSignaturePolicy.Required, [fresh], folder, now).ShouldHaveSingleItem();
        (age.Code, age.Severity).ShouldBe((DscTrustHealthWarnings.CrlAgeCode, "info"));
        age.Message.ShouldContain("issued 5 hours ago");
        var both = DscTrustHealthWarnings.CrlWarnings(PrescriptionSignaturePolicy.Required, [fresh, stale], folder, now);
        both.Single(w => w.Code == DscTrustHealthWarnings.CrlStaleCode).Message.ShouldContain("CN=Old CA: issued 9 days ago, past its next update");
    }

    private static readonly byte[] Content = "{\"format\":\"hc360.prescription.v1\"}"u8.ToArray();

    private byte[] Sign(X509Certificate2 doctor) => DetachedCms.Sign(Content, doctor, DateTimeOffset.UtcNow, silent: true, issuers: [_pki.Ca]);

    private DscCrlCache Cache() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [DscCrlCache.ConfigurationKey] = _folder }).Build(),
        new DataDirectories(new PlatformOptions { DataDirectory = Path.GetTempPath() }),
        TimeProvider.System);

    private static DscRevocationCheck Check(DscCrlCache cache, OnlineRevocationAnswer answer) => new(cache, Online(answer), TimeProvider.System);

    private static IDscOnlineRevocation Online(OnlineRevocationAnswer answer)
    {
        var online = Substitute.For<IDscOnlineRevocation>();
        online.CheckAsync(default!, default!, default!, default).ReturnsForAnyArgs(answer);
        online.FetchCrlAsync(default!, default).ReturnsForAnyArgs((byte[]?)null);
        return online;
    }

    private static byte[] Crl(X509Certificate2 ca, DateTimeOffset thisUpdate, DateTimeOffset nextUpdate, params X509Certificate2[] revoked)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (var certificate in revoked)
        {
            builder.AddEntry(certificate, thisUpdate.AddMinutes(-5));
        }

        return builder.Build(ca, BigInteger.One, nextUpdate, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, thisUpdate);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
