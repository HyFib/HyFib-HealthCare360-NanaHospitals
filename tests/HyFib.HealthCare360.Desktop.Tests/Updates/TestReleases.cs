using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.WindowsInterop.Updates;

namespace HyFib.HealthCare360.Desktop.Tests.Updates;

/// <summary>A throwaway release signing key, made for one test and never written anywhere.</summary>
internal sealed class TestSigningKey : IDisposable
{
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestSigningKey() => Public = ReleaseKey.FromSubjectPublicKeyInfo(_ecdsa.ExportSubjectPublicKeyInfo());

    public ReleaseKey Public { get; }

    public string PrivatePem => _ecdsa.ExportPkcs8PrivateKeyPem();

    /// <summary>The signature file of <paramref name="manifest"/>, as the release workflow writes it.</summary>
    public byte[] Sign(byte[] manifest, string? keyId = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        algorithm = ReleaseSignatureFile.EcdsaP256Sha256,
        keyId = keyId ?? Public.Id,
        signature = Convert.ToBase64String(_ecdsa.SignData(manifest, HashAlgorithmName.SHA256)),
    });

    public void Dispose() => _ecdsa.Dispose();
}

/// <summary>A release package of HC360 Desktop as the release workflow makes one: the zip, its manifest and signature.</summary>
internal sealed record TestPackage(byte[] Zip, byte[] Manifest, byte[] Signature, string Version)
{
    /// <summary>
    /// A package whose HC360Desktop.exe holds its version as text (the tests read versions that way), with the updater,
    /// a file in a folder, and the workstation installer, which is not installed.
    /// </summary>
    public static TestPackage Create(
        TestSigningKey key, string version, string product = ReleasePackages.DesktopProduct, string? exeVersion = null, string? minimumServerVersion = null,
        Action<ZipArchive>? addEntries = null)
    {
        var zip = BuildZip(archive =>
        {
            Add(archive, DesktopUpdateLayout.DesktopExe, exeVersion ?? version);
            Add(archive, DesktopUpdateLayout.UpdaterExe, "updater " + version);
            Add(archive, "runtimes/win-x64/native/WebView2Loader.dll", "loader");
            Add(archive, "Install-HC360Desktop.ps1", "# installer");
            addEntries?.Invoke(archive);
        });
        var manifest = ManifestFor(zip, version, product, minimumServerVersion);
        return new TestPackage(zip, manifest, key.Sign(manifest), version);
    }

    public static byte[] ManifestFor(byte[] zip, string version, string product = ReleasePackages.DesktopProduct, string? minimumServerVersion = null) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = 1,
            product,
            version,
            file = $"HC360-Desktop-{version}-win-x64.zip",
            size = zip.LongLength,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(zip)),
            releasedAt = "2026-10-12T05:30:00Z",
            minimumServerVersion,
        }, new JsonSerializerOptions { WriteIndented = true }));

    public static byte[] BuildZip(Action<ZipArchive> fill)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            fill(archive);
        }

        return buffer.ToArray();
    }

    public static void Add(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    /// <summary>Writes the package as the release has it: <c>HC360-Desktop-&lt;version&gt;-win-x64.zip</c> and its manifest and signature.</summary>
    public string WriteReleaseFiles(string directory)
    {
        Directory.CreateDirectory(directory);
        var zip = Path.Combine(directory, $"HC360-Desktop-{Version}-win-x64.zip");
        File.WriteAllBytes(zip, Zip);
        File.WriteAllBytes(ReleasePackages.ManifestPathFor(zip), Manifest);
        File.WriteAllBytes(ReleasePackages.SignaturePathFor(zip), Signature);
        return zip;
    }

    /// <summary>Writes the package as HC360 Desktop stages it, for the updater.</summary>
    public void WriteStaged(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, DesktopUpdateLayout.PackageFile), Zip);
        File.WriteAllBytes(Path.Combine(directory, DesktopUpdateLayout.ManifestFile), Manifest);
        File.WriteAllBytes(Path.Combine(directory, DesktopUpdateLayout.SignatureFile), Signature);
    }
}

/// <summary>A folder in the temp directory, removed after the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hc360-tests", "upd-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the temp folder.
        }
    }
}
