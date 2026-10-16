using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Settings;
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

/// <summary>
/// System health (DED-17) for DSC signing (ERX-04): while any facility, or the whole hospital, has
/// <c>clinical.prescriptions.signature</c> on, an empty trust folder is a warning — critical where the DSC is required —
/// because DSC signing is refused and signatures already made no longer show or print as DSC signatures (where the DSC is
/// required those prescriptions cannot be printed or shared). That is what a reinstall, a restore on new hardware or a
/// failover to the standby leaves when nobody installs the CCA India roots again: the folder is not in the backups.
/// Files the folder holds that are not certificates are a warning too.
/// </summary>
internal sealed class DscTrustHealthWarnings(HospitalDbContext db, DscTrustStore trust) : IHealthWarningSource
{
    public const string EmptyCode = "prescriptions.dsc_trust_empty";

    public const string UnreadableCode = "prescriptions.dsc_trust_unreadable";

    public async Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken)
    {
        var values = await db.Set<SystemSetting>().AsNoTracking()
            .Where(s => s.Key == PrescriptionSignatureSettings.Key)
            .Select(s => s.Value)
            .ToListAsync(cancellationToken);
        var policies = values.Select(Policy).ToList();
        return Warnings(policies.Contains(PrescriptionSignaturePolicy.Required) ? PrescriptionSignaturePolicy.Required
            : policies.Contains(PrescriptionSignaturePolicy.Optional) ? PrescriptionSignaturePolicy.Optional
            : PrescriptionSignaturePolicy.Off, trust.Current, trust.Folder);
    }

    /// <param name="policy">The strictest setting of any facility or the hospital.</param>
    public static IReadOnlyList<HealthWarning> Warnings(PrescriptionSignaturePolicy policy, DscTrustAnchors anchors, string folder)
    {
        if (policy == PrescriptionSignaturePolicy.Off)
        {
            return [];
        }

        var warnings = new List<HealthWarning>();
        if (anchors.IsEmpty)
        {
            warnings.Add(new(EmptyCode, policy == PrescriptionSignaturePolicy.Required ? "critical" : "warning",
                $"Doctors sign prescriptions with their DSC, but the DSC trust folder ({folder}) holds no CCA India root certificate. DSC signing "
                + "is refused, and signatures already made are not shown or printed as DSC signatures"
                + (policy == PrescriptionSignaturePolicy.Required ? ", so those prescriptions cannot be printed or shared" : string.Empty)
                + ". Install the CCA India roots there (DEPLOYMENT.md, section 4, step 7)."));
        }

        if (anchors.Problems.Count > 0)
        {
            warnings.Add(new(UnreadableCode, "warning",
                $"The DSC trust folder ({folder}) has files that are not certificates: {string.Join("; ", anchors.Problems)}."));
        }

        return warnings;
    }

    private static PrescriptionSignaturePolicy Policy(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return PrescriptionSignatureSettings.Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return PrescriptionSignaturePolicy.Off;
        }
    }
}
