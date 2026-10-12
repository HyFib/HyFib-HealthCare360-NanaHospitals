using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Updates;
using HyFib.HealthCare360.Server;
using HyFib.HealthCare360.WindowsInterop.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// HC360 Desktop updates itself from the Server Node (SRS DED-18): the server takes in only a package signed by a trusted
/// key, and offers and serves it only to enrolled HC360 Desktops. Packages are signed with the run's throwaway key
/// (<see cref="HospitalServer.ReleaseSigningKey"/>); each test starts with nothing offered.
/// </summary>
public sealed class DesktopUpdateTests : IDisposable
{
    private const string OfferPath = "/api/v1/devices/desktop-update";
    private const string PackagePath = "/api/v1/devices/desktop-update/package";

    private readonly HospitalServer _server;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc360-tests", "dsk-" + Guid.NewGuid().ToString("N")[..12]);

    public DesktopUpdateTests(HospitalServer server)
    {
        _server = server;
        Store = _server.Services.GetRequiredService<DesktopPackageStore>();
        RemoveOffer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DesktopPackageStore Store { get; }

    [Fact]
    public async Task Only_an_enrolled_HC360_Desktop_is_told_of_the_update_and_given_the_package()
    {
        var admin = await _server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        Publish(Package("1.0.1"));
        var (workstation, secret) = await EnrolAsync(admin, DeviceKind.Workstation);
        var (agent, agentSecret) = await EnrolAsync(admin, DeviceKind.LabAgent);
        var (revoked, revokedSecret) = await EnrolAsync(admin, DeviceKind.Kiosk);
        await admin.PostAsync($"/api/v1/admin/devices/{revoked}/revoke");

        foreach (var path in new[] { OfferPath, PackagePath })
        {
            (await SendAsync(path, null, null)).ShouldBe(HttpStatusCode.Unauthorized, $"{path} without a credential");
            (await SendAsync(path, workstation, "not-the-secret")).ShouldBe(HttpStatusCode.Unauthorized, $"{path} with a wrong secret");
            (await SendAsync(path, Guid.NewGuid(), secret)).ShouldBe(HttpStatusCode.Unauthorized, $"{path} for a device that does not exist");
            (await SendAsync(path, agent, agentSecret)).ShouldBe(HttpStatusCode.Unauthorized, $"{path} for a lab agent");
            (await SendAsync(path, revoked, revokedSecret)).ShouldBe(HttpStatusCode.Unauthorized, $"{path} for a revoked device");
            (await SendAsync(path, workstation, secret)).ShouldBe(HttpStatusCode.OK, $"{path} for the enrolled workstation");
        }

        // A signed-in member of staff is not a device either, and only device administrators see what is offered. The
        // sign-in comes from the test's own address, so it takes nothing from the sign-in budget other tests share.
        var (staff, _) = await _server.SignInNewSessionAsync(HospitalServer.Users.FrontOffice);
        using (var asked = await staff.SendRawAsync(HttpMethod.Get, OfferPath))
        {
            asked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var state = await staff.SendRawAsync(HttpMethod.Get, "/api/v1/admin/devices/desktop-update");
        state.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Offers_the_package_it_took_in_and_serves_it_unchanged()
    {
        var admin = await _server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (workstation, secret) = await EnrolAsync(admin, DeviceKind.Workstation);
        var store = Store;

        // Nothing offered yet.
        (await SendAsync(OfferPath, workstation, secret)).ShouldBe(HttpStatusCode.NoContent);
        (await SendAsync(PackagePath, workstation, secret)).ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync<DesktopUpdateStateDto>("/api/v1/admin/devices/desktop-update")).ShouldBe(new DesktopUpdateStateDto(null, null, null));

        var package = Package("1.0.1");
        var result = Publish(package);
        result.AlreadyOffered.ShouldBeFalse();
        Publish(package).AlreadyOffered.ShouldBeTrue("the same package again changes nothing");

        // The offer: the manifest and signature as they were signed, which the workstation checks itself.
        using var client = Device(workstation, secret);
        var offer = await client.GetFromJsonAsync<DesktopUpdateOfferDto>(OfferPath, ApiClient.Json, Ct);
        offer.ShouldNotBeNull().Version.ShouldBe("1.0.1");
        offer.Size.ShouldBe(package.Zip.LongLength);
        var manifest = ReleasePackages.ReadManifest(
            Convert.FromBase64String(offer.Manifest), Convert.FromBase64String(offer.Signature), [ServerKey], ReleasePackages.DesktopProduct);
        manifest.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(package.Zip)));

        // The package, whole and from where a dropped download stopped.
        using (var whole = await client.GetAsync(PackagePath, Ct))
        {
            whole.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await whole.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(package.Zip);
            whole.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe($"\"{manifest.Sha256}\"");
        }

        using (var rest = new HttpRequestMessage(HttpMethod.Get, PackagePath))
        {
            rest.Headers.Range = new RangeHeaderValue(100, null);
            rest.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{manifest.Sha256}\""));
            using var partial = await client.SendAsync(rest, Ct);
            partial.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            (await partial.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(package.Zip[100..]);
        }

        // Administration › Devices compares each device's version with this.
        var state = await admin.GetAsync<DesktopUpdateStateDto>("/api/v1/admin/devices/desktop-update");
        state.OfferedVersion.ShouldBe("1.0.1");
        state.Problem.ShouldBeNull();

        // A newer one replaces it.
        Publish(Package("1.0.2"));
        (await client.GetFromJsonAsync<DesktopUpdateOfferDto>(OfferPath, ApiClient.Json, Ct)).ShouldNotBeNull().Version.ShouldBe("1.0.2");
        Directory.GetDirectories(store.Directory).Length.ShouldBe(1, "the package before is removed");
    }

    [Fact]
    public void Refuses_a_package_that_is_unsigned_changed_signed_by_another_key_older_or_not_for_HC360_Desktop()
    {
        var store = Store;
        var good = Package("1.0.1");

        Refused(Write(good with { Signature = [] })).ShouldBe(ReleaseProblem.Unsigned);
        Refused(Write(good with { Manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(good.Manifest).Replace("1.0.1", "1.0.9", StringComparison.Ordinal)) }))
            .ShouldBe(ReleaseProblem.BadSignature);
        var flipped = good.Zip.ToArray();
        flipped[flipped.Length / 2] ^= 0x01;
        Refused(Write(good with { Zip = flipped })).ShouldBe(ReleaseProblem.WrongHash);
        using (var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            Refused(Write(Package("1.0.1", key: stranger))).ShouldBe(ReleaseProblem.UntrustedKey);
        }

        Refused(Write(Package("1.0.1", product: "HC360 Lab agent"))).ShouldBe(ReleaseProblem.WrongProduct);
        Refused(Write(Package("9.0.0", minimumServerVersion: "9.0.0"))).ShouldBe(ReleaseProblem.NeedsNewerServer);
        Directory.Exists(store.Directory).ShouldBeFalse("nothing was taken in");

        Publish(Package("1.0.2"));
        Refused(Write(Package("1.0.1"))).ShouldBe(ReleaseProblem.NotNewer);
        Refused(Write(Package("1.0.2", extra: "another build"))).ShouldBe(ReleaseProblem.NotNewer);

        // A package without its manifest is no release's.
        var zipOnly = Write(good);
        File.Delete(ReleasePackages.ManifestPathFor(zipOnly));
        Should.Throw<FileNotFoundException>(() => store.Publish(zipOnly, SystemHealthService.ProductVersion));
    }

    [Fact]
    public async Task The_command_line_refuses_a_package_signed_by_a_key_this_version_does_not_trust()
    {
        // desktop publish trusts only the keys built in, which no test key is.
        var zip = Write(Package("1.0.1"));

        (await ServerCli.RunAsync(["desktop", "verify", "--package", zip])).ShouldBe(2);
        (await ServerCli.RunAsync(["desktop", "publish", "--package", zip, "--data-dir", Path.Combine(_folder, "data")])).ShouldBe(2);
        Directory.Exists(Path.Combine(_folder, "data", "updates")).ShouldBeFalse();
        (await ServerCli.RunAsync(["desktop", "show", "--data-dir", Path.Combine(_folder, "data")])).ShouldBe(0);
    }

    [Fact]
    public async Task A_package_changed_on_the_server_after_it_was_taken_in_is_no_longer_offered()
    {
        var admin = await _server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (workstation, secret) = await EnrolAsync(admin, DeviceKind.Workstation);
        var store = Store;
        Publish(Package("1.0.1"));
        (await SendAsync(OfferPath, workstation, secret)).ShouldBe(HttpStatusCode.OK);

        var stored = Directory.GetFiles(store.Directory, DesktopPackageStore.PackageFile, SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(stored, Ct);
        bytes[^30] ^= 0x01;
        await File.WriteAllBytesAsync(stored, bytes, Ct);
        File.SetLastWriteTimeUtc(stored, DateTime.UtcNow.AddMinutes(1));

        (await SendAsync(OfferPath, workstation, secret)).ShouldBe(HttpStatusCode.NoContent);
        (await SendAsync(PackagePath, workstation, secret)).ShouldBe(HttpStatusCode.NotFound);
        var state = await admin.GetAsync<DesktopUpdateStateDto>("/api/v1/admin/devices/desktop-update");
        state.OfferedVersion.ShouldBeNull();
        state.Problem.ShouldNotBeNull().ShouldContain("SHA-256");
    }

    public void Dispose()
    {
        RemoveOffer();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private void RemoveOffer()
    {
        if (Directory.Exists(Store.Directory))
        {
            Directory.Delete(Store.Directory, recursive: true);
        }
    }

    private ReleaseKey ServerKey => ReleaseKey.FromSubjectPublicKeyInfo(_server.ReleaseSigningKey.ExportSubjectPublicKeyInfo());

    private DesktopPublishResult Publish(ReleaseFiles package) => Store.Publish(Write(package), SystemHealthService.ProductVersion);

    private ReleaseProblem Refused(string zip) =>
        Should.Throw<ReleaseRejectedException>(() => Store.Publish(zip, SystemHealthService.ProductVersion)).Problem;

    // The package as the release has it: HC360-Desktop-<version>-win-x64.zip, its manifest and signature beside it.
    private string Write(ReleaseFiles package)
    {
        var directory = Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var zip = Path.Combine(directory, $"HC360-Desktop-{package.Version}-win-x64.zip");
        File.WriteAllBytes(zip, package.Zip);
        File.WriteAllBytes(ReleasePackages.ManifestPathFor(zip), package.Manifest);
        if (package.Signature.Length > 0)
        {
            File.WriteAllBytes(ReleasePackages.SignaturePathFor(zip), package.Signature);
        }

        return zip;
    }

    private ReleaseFiles Package(string version, ECDsa? key = null, string product = ReleasePackages.DesktopProduct, string? minimumServerVersion = "1.0.0", string extra = "")
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("HC360Desktop.exe").Open());
            writer.Write($"HC360 Desktop {version} {extra}");
        }

        var zip = buffer.ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = 1,
            product,
            version,
            file = $"HC360-Desktop-{version}-win-x64.zip",
            size = zip.LongLength,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(zip)),
            releasedAt = "2026-10-12T05:30:00Z",
            minimumServerVersion,
        });
        var signer = key ?? _server.ReleaseSigningKey;
        var signature = JsonSerializer.SerializeToUtf8Bytes(new
        {
            algorithm = ReleaseSignatureFile.EcdsaP256Sha256,
            keyId = ReleaseKey.IdOf(signer.ExportSubjectPublicKeyInfo()),
            signature = Convert.ToBase64String(signer.SignData(manifest, HashAlgorithmName.SHA256)),
        });
        return new ReleaseFiles(version, zip, manifest, signature);
    }

    private async Task<(Guid DeviceId, string Secret)> EnrolAsync(ApiClient admin, DeviceKind kind)
    {
        var code = await admin.PostAsync<EnrollmentCodeResponse>("/api/v1/admin/devices/enrollment-codes",
            new CreateEnrollmentCodeRequest(kind, null, "Self-update test", 30));
        var enrolled = await _server.Anonymous(ownAddress: true).PostAsync<EnrollDeviceResponse>("/api/v1/devices/enroll",
            new EnrollDeviceRequest(code.Code, "Update test " + Guid.NewGuid().ToString("N")[..6], "OPD-PC-1", "Windows 11", "1.0.0"));
        return (enrolled.DeviceId, enrolled.DeviceSecret);
    }

    private HttpClient Device(Guid deviceId, string secret)
    {
        var http = _server.Anonymous(ownAddress: true).Http;
        http.DefaultRequestHeaders.Add(DesktopUpdateEndpoints.DeviceIdHeader, deviceId.ToString());
        http.DefaultRequestHeaders.Add(DesktopUpdateEndpoints.DeviceKeyHeader, secret);
        return http;
    }

    private async Task<HttpStatusCode> SendAsync(string path, Guid? deviceId, string? secret)
    {
        using var http = deviceId is { } id ? Device(id, secret!) : _server.Anonymous(ownAddress: true).Http;
        using var response = await http.GetAsync(path, Ct);
        return response.StatusCode;
    }

    private sealed record ReleaseFiles(string Version, byte[] Zip, byte[] Manifest, byte[] Signature);
}
