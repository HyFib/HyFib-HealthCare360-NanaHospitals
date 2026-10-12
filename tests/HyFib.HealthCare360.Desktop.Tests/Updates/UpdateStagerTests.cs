using System.IO;
using HyFib.HealthCare360.Desktop.Services;
using HyFib.HealthCare360.WindowsInterop.Updates;

namespace HyFib.HealthCare360.Desktop.Tests.Updates;

/// <summary>
/// HC360 Desktop stages only a package that verifies and is newer than itself, goes on with a download that was cut off,
/// and drops one that does not match its manifest (SRS DED-18).
/// </summary>
public sealed class UpdateStagerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TestSigningKey _key = new();

    private string Staging => _folder.Combine("Updates");

    [Fact]
    public async Task Stages_a_newer_signed_package_for_the_updater()
    {
        var package = TestPackage.Create(_key, "1.2.0");
        var stager = Stager("1.1.0");

        var staged = await stager.StageAsync(package.Manifest, package.Signature, Serve(package.Zip), Ct);

        staged.ShouldNotBeNull().Version.ShouldBe("1.2.0");
        File.ReadAllBytes(Path.Combine(Staging, DesktopUpdateLayout.PackageFile)).ShouldBe(package.Zip);
        File.ReadAllBytes(Path.Combine(Staging, DesktopUpdateLayout.ManifestFile)).ShouldBe(package.Manifest);
        File.ReadAllBytes(Path.Combine(Staging, DesktopUpdateLayout.SignatureFile)).ShouldBe(package.Signature);
        Directory.GetFiles(Staging, "*.partial").ShouldBeEmpty();
        Stager("1.1.0").Ready().ShouldNotBeNull().Version.ShouldBe("1.2.0");

        // Asked again, it downloads nothing more.
        (await stager.StageAsync(package.Manifest, package.Signature, (_, _, _) => throw new InvalidOperationException("downloaded again"), Ct))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task Goes_on_with_a_download_that_was_cut_off()
    {
        var package = TestPackage.Create(_key, "1.2.0");
        var cutOff = package.Zip.Length / 3;
        long resumedFrom = -1;
        var stager = Stager("1.1.0");

        await Should.ThrowAsync<IOException>(() => stager.StageAsync(package.Manifest, package.Signature, async (file, _, ct) =>
        {
            await file.WriteAsync(package.Zip.AsMemory(0, cutOff), ct);
            throw new IOException("the WiFi dropped");
        }, Ct));
        var staged = await stager.StageAsync(package.Manifest, package.Signature, async (file, _, ct) =>
        {
            resumedFrom = file.Position;
            await file.WriteAsync(package.Zip.AsMemory((int)file.Position), ct);
        }, Ct);

        resumedFrom.ShouldBe(cutOff);
        staged.ShouldNotBeNull();
        File.ReadAllBytes(Path.Combine(Staging, DesktopUpdateLayout.PackageFile)).ShouldBe(package.Zip);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_manifest_is_not_staged()
    {
        var package = TestPackage.Create(_key, "1.2.0");
        var other = TestPackage.Create(_key, "1.2.0", addEntries: archive => TestPackage.Add(archive, "planted.dll", "not HyFib's"));
        var stager = Stager("1.1.0");

        var refused = await Should.ThrowAsync<ReleaseRejectedException>(() => stager.StageAsync(package.Manifest, package.Signature, Serve(other.Zip), Ct));

        refused.Problem.ShouldBeOneOf(ReleaseProblem.WrongHash, ReleaseProblem.WrongSize);
        File.Exists(Path.Combine(Staging, DesktopUpdateLayout.ManifestFile)).ShouldBeFalse();
        stager.Ready().ShouldBeNull();

        // The next try downloads it all again.
        (await stager.StageAsync(package.Manifest, package.Signature, Serve(package.Zip), Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Nothing_is_downloaded_for_a_package_that_is_not_newer_or_not_trusted()
    {
        var same = TestPackage.Create(_key, "1.2.0");
        (await Stager("1.2.0").StageAsync(same.Manifest, same.Signature, (_, _, _) => throw new InvalidOperationException("downloaded"), Ct)).ShouldBeNull();

        using var stranger = new TestSigningKey();
        var untrusted = TestPackage.Create(stranger, "1.3.0");
        (await Should.ThrowAsync<ReleaseRejectedException>(() =>
                Stager("1.2.0").StageAsync(untrusted.Manifest, untrusted.Signature, (_, _, _) => throw new InvalidOperationException("downloaded"), Ct)))
            .Problem.ShouldBe(ReleaseProblem.UntrustedKey);
        Directory.Exists(Staging).ShouldBeFalse();
    }

    [Fact]
    public async Task Once_the_update_is_installed_the_staged_package_is_removed()
    {
        var package = TestPackage.Create(_key, "1.2.0");
        await Stager("1.1.0").StageAsync(package.Manifest, package.Signature, Serve(package.Zip), Ct);

        Stager("1.1.0").CleanUp(TimeSpan.FromDays(7));
        File.Exists(Path.Combine(Staging, DesktopUpdateLayout.PackageFile)).ShouldBeTrue("it is not installed yet");

        var updated = Stager("1.2.0");
        updated.Ready().ShouldBeNull();
        updated.CleanUp(TimeSpan.FromDays(7));
        Directory.GetFiles(Staging).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_staged_package_changed_afterwards_is_no_longer_ready()
    {
        var package = TestPackage.Create(_key, "1.2.0");
        await Stager("1.1.0").StageAsync(package.Manifest, package.Signature, Serve(package.Zip), Ct);
        var changed = package.Zip.ToArray();
        changed[^10] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(Staging, DesktopUpdateLayout.PackageFile), changed);

        Stager("1.1.0").Ready().ShouldBeNull();
    }

    public void Dispose()
    {
        _key.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UpdateStager Stager(string installed) => new(Staging, [_key.Public], installed);

    private static Func<FileStream, ReleaseManifest, CancellationToken, Task> Serve(byte[] zip) =>
        async (file, _, ct) => await file.WriteAsync(zip.AsMemory((int)file.Position), ct);
}
