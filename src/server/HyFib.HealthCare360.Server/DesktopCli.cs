using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Updates;
using HyFib.HealthCare360.WindowsInterop.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// The HC360 Desktop package the server offers to workstations (SRS DED-18): <c>desktop publish</c> takes one in,
/// <c>desktop verify</c> only checks one, and <c>desktop show</c> says what is offered. Packages are checked against the
/// release keys built into this version, as the server and HC360 Desktop check them.
/// Exit codes: 0 done, 1 a file is missing or cannot be read, 2 the package was refused.
/// </summary>
internal static class DesktopCli
{
    public const string Usage = """
          desktop publish --package <HC360-Desktop-<version>-win-x64.zip> [--data-dir <folder>]
              Offers this HC360 Desktop package to the enrolled workstations, which then update themselves from the server
              (SRS DED-18). Its manifest and signature (.manifest.json, .manifest.sig) must be beside it, as the release has
              them: the package must be signed by HyFib, for HC360 Desktop, unchanged, not older than the one offered, and for
              this version of the server. Nothing changes when it is refused. Install-HC360Server.ps1 does this for the
              package that sits beside the server package.

          desktop verify --package <HC360-Desktop-<version>-win-x64.zip>
              Checks a package as "desktop publish" does, and changes nothing.

          desktop show [--data-dir <folder>]
              Says which HC360 Desktop version the server offers to workstations, or why it offers none.
        """;

    public static int Publish(string package, string? dataDirectory)
    {
        var store = Store(dataDirectory);
        return Run(() =>
        {
            var result = store.Publish(Path.GetFullPath(package), SystemHealthService.ProductVersion);
            Console.WriteLine(result.AlreadyOffered
                ? $"The server offers HC360 Desktop {result.Manifest.Version} already: nothing changed."
                : $"The server offers HC360 Desktop {result.Manifest.Version} to the enrolled workstations from now on. Each takes it at its next check (within 4 hours), and installs it when HC360 Desktop is next closed or the computer next starts.");
        });
    }

    public static int Verify(string package) => Run(() =>
    {
        var path = Path.GetFullPath(package);
        var manifestPath = ReleasePackages.ManifestPathFor(path);
        var signaturePath = ReleasePackages.SignaturePathFor(path);
        if (!File.Exists(path) || !File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"{path} or its manifest ({Path.GetFileName(manifestPath)}) is missing.");
        }

        var manifest = ReleasePackages.ReadManifest(
            File.ReadAllBytes(manifestPath), File.Exists(signaturePath) ? File.ReadAllBytes(signaturePath) : [], ReleaseTrust.BuiltIn.Keys, ReleasePackages.DesktopProduct);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ReleasePackages.CheckPackage(stream, manifest);
        }

        Console.WriteLine($"{Path.GetFileName(path)} is HC360 Desktop {manifest.Version}, signed by HyFib and unchanged (for servers {manifest.MinimumServerVersion ?? "of any version"}).");
    });

    public static async Task<int> ShowAsync(string? dataDirectory)
    {
        var (offer, problem) = await Store(dataDirectory).CheckAsync(SystemHealthService.ProductVersion, CancellationToken.None);
        Console.WriteLine(offer is not null
            ? $"The server offers HC360 Desktop {offer.Manifest.Version} (released {offer.Manifest.ReleasedAt:yyyy-MM-dd}) to the enrolled workstations."
            : problem is not null
                ? $"The server offers no HC360 Desktop update: {problem}"
                : "The server offers no HC360 Desktop update: none has been put on it (desktop publish).");
        return offer is not null || problem is null ? 0 : 2;
    }

    private static DesktopPackageStore Store(string? dataDirectory) =>
        new(new DataDirectories(new PlatformOptions { DataDirectory = dataDirectory }), ReleaseTrust.BuiltIn, NullLogger<DesktopPackageStore>.Instance);

    private static int Run(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (ReleaseRejectedException ex)
        {
            Console.Error.WriteLine($"Refused: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
