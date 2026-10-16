using System.IO;
using System.Text;
using HyFib.HealthCare360.WindowsInterop.Updates;

namespace HyFib.HealthCare360.Desktop.Tests.Updates;

/// <summary>
/// The checks every part of HC360 makes before it trusts a package HC360 Desktop installs by itself (SRS DED-18): a
/// manifest signed by a trusted key, for HC360 Desktop, a package that matches it, and nothing older than what is there.
/// </summary>
public sealed class ReleasePackageTests
{
    [Fact]
    public void A_package_signed_by_a_trusted_key_and_unchanged_verifies()
    {
        using var key = new TestSigningKey();
        var package = TestPackage.Create(key, "1.2.0", minimumServerVersion: "1.2.0");

        var manifest = ReleasePackages.ReadManifest(package.Manifest, package.Signature, [key.Public], ReleasePackages.DesktopProduct);
        using var zip = new MemoryStream(package.Zip);
        ReleasePackages.CheckPackage(zip, manifest);

        manifest.Product.ShouldBe("HC360 Desktop");
        manifest.Version.ShouldBe("1.2.0");
        manifest.Size.ShouldBe(package.Zip.LongLength);
        manifest.File.ShouldBe("HC360-Desktop-1.2.0-win-x64.zip");
        manifest.MinimumServerVersion.ShouldBe("1.2.0");
    }

    [Fact]
    public void A_changed_manifest_is_refused()
    {
        using var key = new TestSigningKey();
        var package = TestPackage.Create(key, "1.2.0");
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(package.Manifest).Replace("1.2.0", "9.9.9", StringComparison.Ordinal));

        Problem(() => ReleasePackages.ReadManifest(changed, package.Signature, [key.Public], ReleasePackages.DesktopProduct))
            .ShouldBe(ReleaseProblem.BadSignature);

        // One byte of whitespace is a change too: the manifest's bytes are signed as they are.
        Problem(() => ReleasePackages.ReadManifest([.. package.Manifest, (byte)' '], package.Signature, [key.Public], ReleasePackages.DesktopProduct))
            .ShouldBe(ReleaseProblem.BadSignature);
    }

    [Fact]
    public void A_changed_package_is_refused_by_its_hash_or_size()
    {
        using var key = new TestSigningKey();
        var package = TestPackage.Create(key, "1.2.0");
        var manifest = ReleasePackages.ReadManifest(package.Manifest, package.Signature, [key.Public], ReleasePackages.DesktopProduct);

        var flipped = package.Zip.ToArray();
        flipped[flipped.Length / 2] ^= 0x01;
        Problem(() => ReleasePackages.CheckPackage(new MemoryStream(flipped), manifest)).ShouldBe(ReleaseProblem.WrongHash);
        Problem(() => ReleasePackages.CheckPackage(new MemoryStream(package.Zip[..^1]), manifest)).ShouldBe(ReleaseProblem.WrongSize);
        Problem(() => ReleasePackages.CheckPackage(new MemoryStream([.. package.Zip, 0]), manifest)).ShouldBe(ReleaseProblem.WrongSize);
    }

    [Fact]
    public void A_package_signed_with_a_key_that_is_not_trusted_is_refused()
    {
        using var hyfib = new TestSigningKey();
        using var someoneElse = new TestSigningKey();
        var package = TestPackage.Create(someoneElse, "1.2.0");

        var refused = Should.Throw<ReleaseRejectedException>(() =>
            ReleasePackages.ReadManifest(package.Manifest, package.Signature, [hyfib.Public], ReleasePackages.DesktopProduct));
        refused.Problem.ShouldBe(ReleaseProblem.UntrustedKey);
        refused.Message.ShouldContain(someoneElse.Public.Id);

        // Nor does naming a trusted key's id help: the signature must be that key's.
        var claimed = someoneElse.Sign(package.Manifest, keyId: hyfib.Public.Id);
        Problem(() => ReleasePackages.ReadManifest(package.Manifest, claimed, [hyfib.Public], ReleasePackages.DesktopProduct))
            .ShouldBe(ReleaseProblem.BadSignature);

        // And no key is trusted until HyFib adds its own to ReleaseKeys.
        Problem(() => ReleasePackages.ReadManifest(package.Manifest, package.Signature, [], ReleasePackages.DesktopProduct))
            .ShouldBe(ReleaseProblem.UntrustedKey);
    }

    [Fact]
    public void After_a_key_rotation_packages_signed_with_the_old_key_and_the_new_one_both_verify()
    {
        using var old = new TestSigningKey();
        using var rotated = new TestSigningKey();
        IReadOnlyList<ReleaseKey> trusted = [old.Public, rotated.Public];

        // One package: two would each stamp their zip entries with the time they were made, so their manifests can differ.
        var signedWithOldBefore = TestPackage.Create(old, "1.2.0");
        ReleasePackages.ReadManifest(signedWithOldBefore.Manifest, signedWithOldBefore.Signature, trusted, ReleasePackages.DesktopProduct)
            .Version.ShouldBe("1.2.0");
        var signedWithNew = TestPackage.Create(rotated, "1.3.0");
        ReleasePackages.ReadManifest(signedWithNew.Manifest, signedWithNew.Signature, trusted, ReleasePackages.DesktopProduct).Version.ShouldBe("1.3.0");

        // Once the old key is removed from the list, its packages are no longer taken.
        var signedWithOld = TestPackage.Create(old, "1.3.1");
        Problem(() => ReleasePackages.ReadManifest(signedWithOld.Manifest, signedWithOld.Signature, [rotated.Public], ReleasePackages.DesktopProduct))
            .ShouldBe(ReleaseProblem.UntrustedKey);
    }

    [Fact]
    public void Unsigned_and_unreadable_signatures_are_refused()
    {
        using var key = new TestSigningKey();
        var package = TestPackage.Create(key, "1.2.0");

        Problem(() => ReleasePackages.ReadManifest(package.Manifest, [], [key.Public], ReleasePackages.DesktopProduct)).ShouldBe(ReleaseProblem.Unsigned);
        Problem(() => ReleasePackages.ReadManifest(package.Manifest, "not json"u8.ToArray(), [key.Public], ReleasePackages.DesktopProduct))
            .ShouldBe(ReleaseProblem.UnreadableSignature);
        var otherAlgorithm = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(package.Signature).Replace(ReleaseSignatureFile.EcdsaP256Sha256, "RSA-SHA1", StringComparison.Ordinal));
        Problem(() => ReleasePackages.ReadManifest(package.Manifest, otherAlgorithm, [key.Public], ReleasePackages.DesktopProduct))
            .ShouldBe(ReleaseProblem.UnreadableSignature);
    }

    [Fact]
    public void A_package_of_another_product_is_refused()
    {
        using var key = new TestSigningKey();
        var agent = TestPackage.Create(key, "1.2.0", product: "HC360 Lab agent");

        var refused = Should.Throw<ReleaseRejectedException>(() =>
            ReleasePackages.ReadManifest(agent.Manifest, agent.Signature, [key.Public], ReleasePackages.DesktopProduct));

        refused.Problem.ShouldBe(ReleaseProblem.WrongProduct);
        refused.Message.ShouldContain("HC360 Lab agent");
    }

    [Fact]
    public void A_signed_manifest_that_says_nothing_usable_is_refused()
    {
        using var key = new TestSigningKey();
        var manifest = """{ "format": 1, "product": "HC360 Desktop", "version": "1.2.0", "file": "..\\evil.zip", "size": 10, "sha256": "00" }"""u8.ToArray();

        Problem(() => ReleasePackages.ReadManifest(manifest, key.Sign(manifest), [key.Public], ReleasePackages.DesktopProduct))
            .ShouldBe(ReleaseProblem.UnreadableManifest);
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.1")]
    [InlineData("1.2.0", "1.10.0")]
    [InlineData("1.3.0-rc.1", "1.3.0")]
    public void Nothing_older_than_or_as_old_as_the_installed_version_is_taken(string offered, string installed)
    {
        using var key = new TestSigningKey();
        var package = TestPackage.Create(key, offered);
        var manifest = ReleasePackages.ReadManifest(package.Manifest, package.Signature, [key.Public], ReleasePackages.DesktopProduct);

        Problem(() => ReleasePackages.CheckNewer(manifest, installed)).ShouldBe(ReleaseProblem.NotNewer);
    }

    [Theory]
    [InlineData("1.2.1", "1.2.0")]
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("1.3.0", "1.3.0-rc.2")]
    [InlineData("1.3.0-rc.10", "1.3.0-rc.9")]
    [InlineData("2.0.0", "1.99.99.99")]
    public void A_newer_version_is_taken(string offered, string installed)
    {
        using var key = new TestSigningKey();
        var package = TestPackage.Create(key, offered);
        var manifest = ReleasePackages.ReadManifest(package.Manifest, package.Signature, [key.Public], ReleasePackages.DesktopProduct);

        Should.NotThrow(() => ReleasePackages.CheckNewer(manifest, installed));
    }

    [Fact]
    public void A_package_that_needs_a_newer_server_is_refused_by_an_older_one()
    {
        using var key = new TestSigningKey();
        var package = TestPackage.Create(key, "1.3.0", minimumServerVersion: "1.3.0");
        var manifest = ReleasePackages.ReadManifest(package.Manifest, package.Signature, [key.Public], ReleasePackages.DesktopProduct);

        Problem(() => ReleasePackages.CheckServer(manifest, "1.2.9")).ShouldBe(ReleaseProblem.NeedsNewerServer);
        Should.NotThrow(() => ReleasePackages.CheckServer(manifest, "1.3.0"));
        Should.NotThrow(() => ReleasePackages.CheckServer(manifest, "1.4.0"));
    }

    [Fact]
    public void Versions_compare_as_release_numbers()
    {
        ReleaseVersion.Parse("1.2").ShouldBe(ReleaseVersion.Parse("1.2.0"));
        ReleaseVersion.Parse("1.2.0+abc123").ShouldBe(ReleaseVersion.Parse("1.2.0"));
        (ReleaseVersion.Parse("1.2.0-beta") < ReleaseVersion.Parse("1.2.0-rc.1")).ShouldBeTrue();
        ReleaseVersion.Parse("1.2.3-rc.1").ToString().ShouldBe("1.2.3-rc.1");
        ReleaseVersion.TryParse("1", out _).ShouldBeFalse();
        ReleaseVersion.TryParse("1.x.0", out _).ShouldBeFalse();
        ReleaseVersion.TryParse("1.2.0-", out _).ShouldBeFalse();
        ReleaseVersion.TryParse("1.2.0-rc 1", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_release_key_is_a_P256_public_key()
    {
        using var key = new TestSigningKey();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);

        ReleaseKey.FromPem(System.Security.Cryptography.PemEncoding.WriteString("PUBLIC KEY", SubjectPublicKeyInfo(key))).Id.ShouldBe(key.Public.Id);
        key.Public.Id.Length.ShouldBe(16);
        Should.Throw<ArgumentException>(() => ReleaseKey.FromSubjectPublicKeyInfo(rsa.ExportSubjectPublicKeyInfo()));
        Should.Throw<ArgumentException>(() => ReleaseKey.FromSubjectPublicKeyInfo(p384.ExportSubjectPublicKeyInfo()));
        Should.Throw<ArgumentException>(() => ReleaseKey.FromPem(key.PrivatePem));
    }

    [Fact]
    public void The_built_in_release_keys_load()
    {
        // A key pasted into ReleaseKeys wrongly would stop every check (the list fails to load). With none, nothing is
        // installed by itself.
        ReleaseKeys.Trusted.Select(k => k.Id).ShouldAllBe(id => id.Length == 16);
        ReleaseKeys.Trusted.Select(k => k.Id).ShouldBeUnique();
    }

    private static byte[] SubjectPublicKeyInfo(TestSigningKey key)
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportFromPem(key.PrivatePem);
        return ecdsa.ExportSubjectPublicKeyInfo();
    }

    private static ReleaseProblem Problem(Action action) => Should.Throw<ReleaseRejectedException>(action).Problem;

    private static ReleaseProblem Problem(Func<object> action) => Should.Throw<ReleaseRejectedException>(action).Problem;
}
