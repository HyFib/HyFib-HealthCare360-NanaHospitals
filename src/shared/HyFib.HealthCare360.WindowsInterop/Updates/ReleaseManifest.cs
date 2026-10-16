using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyFib.HealthCare360.WindowsInterop.Updates;

/// <summary>
/// What a release says about a package HC360 may install by itself (SRS DED-18): the product, its version and the exact
/// file (name, size and SHA-256). The release workflow writes it next to the package, as
/// <c>&lt;package&gt;.manifest.json</c>, and signs its bytes, as they are, into <c>&lt;package&gt;.manifest.sig</c>
/// (<see cref="ReleaseSignatureFile"/>). The manifest is read only once its signature verifies (<see cref="ReleasePackages"/>).
/// </summary>
/// <param name="Format">1.</param>
/// <param name="Product">What the package installs: <see cref="ReleasePackages.DesktopProduct"/>.</param>
/// <param name="MinimumServerVersion">The oldest server this version works with; the server does not offer it before.</param>
public sealed record ReleaseManifest(
    int Format,
    string Product,
    string Version,
    string File,
    long Size,
    string Sha256,
    DateTimeOffset ReleasedAt,
    string? MinimumServerVersion)
{
    public const int CurrentFormat = 1;

    /// <summary>The manifest's version, which <see cref="ReleasePackages.ReadManifest"/> has checked parses.</summary>
    [JsonIgnore]
    public ReleaseVersion ReleaseVersion => ReleaseVersion.Parse(Version);
}

/// <summary>
/// The detached signature of a manifest (<c>&lt;package&gt;.manifest.sig</c>): the algorithm, the id of the key that
/// signed (<see cref="ReleaseKey.Id"/>) and the signature of the manifest file's bytes, in base64.
/// </summary>
public sealed record ReleaseSignatureFile(string Algorithm, string KeyId, string Signature)
{
    public const string EcdsaP256Sha256 = "ECDSA-P256-SHA256";
}

/// <summary>What the update files hold, read and written without reflection, so the trimmed updater can use them too.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(ReleaseSignatureFile))]
[JsonSerializable(typeof(DesktopUpdateStatus))]
internal sealed partial class UpdatesJson : JsonSerializerContext;
