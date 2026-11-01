using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.WindowsInterop.Trust;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>
/// What enrolment leaves on the lab PC (<c>%ProgramData%\HyFib\HC360LabAgent\agent.json</c>): the server address, the
/// hospital CA it must chain to, and the device identity. The device secret is protected with machine DPAPI, because a
/// service has no signed-in user to protect it with; the install script makes the folder readable only by SYSTEM,
/// Administrators and the agent's own service account, and the protected secret cannot be read on another PC.
/// </summary>
internal sealed class AgentConfiguration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("HC360LabAgent.DeviceSecret.v1");

    public string? ServerUrl { get; set; }

    /// <summary>The hospital's private CA certificate (public data) that the server's certificate must chain to.</summary>
    public string? CaCertificatePem { get; set; }

    public string? CaFingerprint { get; set; }

    /// <summary>
    /// The next CA the server prepared ('certificates prepare-ca'), held on the word of the CA pinned: a second pin until the
    /// server switches to it, and then the CA pinned (CA-ROLLOVER.md).
    /// </summary>
    public string? NextCaCertificatePem { get; set; }

    /// <summary>
    /// The CA that <see cref="CaCertificatePem"/> replaced in a switch the agent followed: a second pin until it expires
    /// (CA-ROLLOVER.md).
    /// </summary>
    public string? PreviousCaCertificatePem { get; set; }

    public Guid? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    public string? ProtectedDeviceSecret { get; set; }

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyFib", "HC360LabAgent", "agent.json");

    [JsonIgnore]
    public Uri? ServerUri => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    [JsonIgnore]
    public bool IsEnrolled => ServerUri is not null && !string.IsNullOrWhiteSpace(CaCertificatePem) && DeviceId is not null && !string.IsNullOrEmpty(ProtectedDeviceSecret);

    /// <summary>The device secret, or null when it cannot be read (for example the file was copied from another PC).</summary>
    [JsonIgnore]
    public string? DeviceSecret
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedDeviceSecret))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedDeviceSecret), SecretEntropy, DataProtectionScope.LocalMachine));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null;
            }
        }
        set => ProtectedDeviceSecret = value is null
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), SecretEntropy, DataProtectionScope.LocalMachine));
    }

    /// <summary>
    /// Takes <paramref name="step"/> of the hospital CA's rollover and saves it to <paramref name="path"/>: holds the next CA
    /// the server prepared, or pins it once the server switched to it, keeping the CA it replaced. When the file cannot be
    /// written the configuration stays as it was, and the exception is thrown: the next check takes the step again.
    /// </summary>
    public void Take(HospitalCaStep step, string path)
    {
        ArgumentNullException.ThrowIfNull(step);
        var before = (CaCertificatePem, CaFingerprint, NextCaCertificatePem, PreviousCaCertificatePem);
        switch (step)
        {
            case HospitalCaStep.HoldNext hold:
                NextCaCertificatePem = hold.Next.CaPem;
                break;
            case HospitalCaStep.SwitchToNext switched when NextCaCertificatePem is { } next:
                (PreviousCaCertificatePem, CaCertificatePem, CaFingerprint, NextCaCertificatePem) = (CaCertificatePem, next, switched.Fingerprint, null);
                break;
            default:
                return;
        }

        try
        {
            Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (CaCertificatePem, CaFingerprint, NextCaCertificatePem, PreviousCaCertificatePem) = before;
            throw;
        }
    }

    /// <summary>The saved configuration; an empty one when there is none yet or it cannot be read.</summary>
    public static AgentConfiguration Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(path), Json) ?? new AgentConfiguration() : new AgentConfiguration();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AgentConfiguration();
        }
    }

    /// <summary>Written to a temporary file first, so a power cut never leaves half a configuration.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
