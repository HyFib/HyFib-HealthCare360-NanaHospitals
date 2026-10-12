using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// Per-user configuration in <c>%LocalAppData%\HyFib\HC360Desktop\settings.json</c>. The device secret is protected
/// with Windows DPAPI (current user), so copying the file to another PC or account does not copy the identity.
/// IT can pre-provision the server, kiosk mode and the expected CA fingerprint with a read-only machine policy
/// (<c>%ProgramData%\HyFib\HC360Desktop\policy.json</c>).
/// </summary>
internal sealed class DesktopSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("HC360Desktop.DeviceSecret.v1");

    public string? ServerUrl { get; set; }

    /// <summary>
    /// Addresses of the configured server to try when <see cref="ServerUrl"/> does not answer (SRS DED-03): those it
    /// answered on, most recent first, then the others discovery found for it at setup. Each is used only once it
    /// answers over a connection pinned to the hospital CA (<see cref="ServerFallback"/>).
    /// </summary>
    public List<string> ServerAddresses { get; set; } = [];

    /// <summary>The hospital's private CA certificate (public data) that every server certificate must chain to.</summary>
    public string? CaCertificatePem { get; set; }

    public string? CaFingerprint { get; set; }

    public Guid? DeviceId { get; set; }

    public string? ProtectedDeviceSecret { get; set; }

    public string? DeviceName { get; set; }

    public bool Kiosk { get; set; }

    public string StartPath { get; set; } = "/";

    public bool AllowDevTools { get; set; }

    /// <summary>Expected CA fingerprint from the IT policy (setup refuses any other server).</summary>
    [JsonIgnore]
    public string? PolicyCaFingerprint { get; private set; }

    [JsonIgnore]
    public bool ServerFixedByPolicy { get; private set; }

    [JsonIgnore]
    public Uri? ServerUri => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    [JsonIgnore]
    public bool IsConfigured => ServerUri is not null && !string.IsNullOrWhiteSpace(CaCertificatePem);

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
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedDeviceSecret), SecretEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                Log.Warning(ex, "The stored device secret could not be decrypted; the device must be enrolled again");
                return null;
            }
        }
        set => ProtectedDeviceSecret = value is null
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), SecretEntropy, DataProtectionScope.CurrentUser));
    }

    public static string AppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyFib", "HC360Desktop");

    public static string AppVersion { get; } =
        typeof(DesktopSettings).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    private static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    private static string PolicyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyFib", "HC360Desktop", "policy.json");

    public static DesktopSettings Load()
    {
        DesktopSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath), Json) ?? new DesktopSettings()
                : new DesktopSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Settings could not be read; starting with defaults");
            settings = new DesktopSettings();
        }

        settings.ApplyPolicy();
        return settings;
    }

    /// <summary>Puts <paramref name="server"/> first among <see cref="ServerAddresses"/>; true when that changed them.</summary>
    public bool RememberServer(Uri server)
    {
        var addresses = ServerFallback.Remember(ServerAddresses, server);
        if (addresses.SequenceEqual(ServerAddresses, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        ServerAddresses = [.. addresses];
        return true;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDirectory);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, SettingsPath, overwrite: true);
    }

    private void ApplyPolicy()
    {
        if (!File.Exists(PolicyPath))
        {
            return;
        }

        try
        {
            var policy = JsonSerializer.Deserialize<DesktopPolicy>(File.ReadAllText(PolicyPath), Json);
            if (policy is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(policy.ServerUrl))
            {
                if (!string.Equals(ServerUrl, policy.ServerUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // A different server than the one configured: trust must be established again.
                    CaCertificatePem = null;
                    CaFingerprint = null;
                }

                ServerUrl = policy.ServerUrl;
                ServerFixedByPolicy = true;
            }

            PolicyCaFingerprint = policy.CaFingerprint?.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            if (PolicyCaFingerprint is not null && CaFingerprint is not null && !string.Equals(PolicyCaFingerprint, CaFingerprint, StringComparison.Ordinal))
            {
                Log.Warning("The pinned CA does not match the IT policy; setup is required");
                CaCertificatePem = null;
                CaFingerprint = null;
            }

            if (policy.Kiosk is { } kiosk)
            {
                Kiosk = kiosk;
            }

            if (!string.IsNullOrWhiteSpace(policy.StartPath) && DeepLinks.IsSafePath(policy.StartPath))
            {
                StartPath = policy.StartPath;
            }

            if (policy.DisableDevTools == true)
            {
                AllowDevTools = false;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "The machine policy could not be read");
        }
    }

    private sealed record DesktopPolicy(string? ServerUrl, string? CaFingerprint, bool? Kiosk, string? StartPath, bool? DisableDevTools);
}
