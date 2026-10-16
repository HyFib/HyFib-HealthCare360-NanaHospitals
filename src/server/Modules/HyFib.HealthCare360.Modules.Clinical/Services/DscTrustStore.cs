using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The server's DSC trust folder (ERX-04): <c>HealthCare360:Clinical:DscTrustDirectory</c> in the server's configuration
/// (a relative path is taken from the data folder), by default <c>certs\dsc-trust</c> in the data folder. IT puts the
/// CCA India root certificates there, downloaded from cca.gov.in with their thumbprints checked against the ones the CCA
/// publishes; a doctor's DSC signature is accepted only when the certificate chains to one of them
/// (<see cref="DscTrustAnchors"/>). Until then DSC signing is refused with a message saying so. The folder is read again
/// when its files change, so adding a root needs no restart.
/// </summary>
public sealed class DscTrustStore(IConfiguration configuration, DataDirectories directories)
{
    public const string ConfigurationKey = "HealthCare360:Clinical:DscTrustDirectory";

    public const string DefaultFolderName = "dsc-trust";

    private readonly Lock _gate = new();
    private string? _stamp;
    private DscTrustAnchors _anchors = DscTrustAnchors.None;

    // A service's working directory is System32, so a relative path is taken from the data folder instead.
    public string Folder { get; } = configuration[ConfigurationKey] is { Length: > 0 } configured
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), directories.Root)
        : Path.Combine(directories.Certificates, DefaultFolderName);

    /// <summary>The certificates in the folder now.</summary>
    public DscTrustAnchors Current
    {
        get
        {
            var stamp = Stamp();
            lock (_gate)
            {
                if (_stamp != stamp)
                {
                    _anchors = DscTrustAnchors.Load(Folder);
                    _stamp = stamp;
                }

                return _anchors;
            }
        }
    }

    // The files' names, sizes and times: a cheap test for a change, made on every check.
    private string Stamp()
    {
        try
        {
            return string.Join('|', DscTrustAnchors.Files(Folder).Select(path =>
            {
                var file = new FileInfo(path);
                return $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
