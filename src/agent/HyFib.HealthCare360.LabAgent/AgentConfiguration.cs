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
    /// The CA that <see cref="CaCertificatePem"/> replaced in a rollover the agent followed: a second pin until it expires
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

    /// <summary>Pins <paramref name="successor"/>, the new CA after a rollover, keeping the one it replaced.</summary>
    public void Follow(HospitalCaSuccessor successor)
    {
        PreviousCaCertificatePem = CaCertificatePem;
        CaCertificatePem = successor.CaPem;
        CaFingerprint = successor.Fingerprint;
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
