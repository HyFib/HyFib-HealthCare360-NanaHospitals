using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.WindowsInterop.Trust;
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

    /// <summary>
    /// The next CA the server prepared ('certificates prepare-ca'), held on the word of the CA pinned: a second pin until the
    /// server switches to it, and then the CA pinned (CA-ROLLOVER.md).
    /// </summary>
    public string? NextCaCertificatePem { get; set; }

    /// <summary>The certificate the pinned CA signed for the next CA's key: that the next CA succeeds it.</summary>
    public string? NextCaCrossCertificatePem { get; set; }

    /// <summary>
    /// The CA that <see cref="CaCertificatePem"/> replaced in a switch this workstation followed: a second pin until it
    /// expires, and what an IT policy that still names it is checked against (CA-ROLLOVER.md).
    /// </summary>
    public string? PreviousCaCertificatePem { get; set; }

    /// <summary>The certificate the previous CA signed for the current CA's key: that this CA succeeded it.</summary>
    public string? CaCrossCertificatePem { get; set; }

    public Guid? DeviceId { get; set; }

    public string? ProtectedDeviceSecret { get; set; }

    public string? DeviceName { get; set; }

    public bool Kiosk { get; set; }

    public string StartPath { get; set; } = "/";

    /// <summary>
    /// DevTools in the staff app, only where IT's machine policy allows them (<c>"allowDevTools": true</c>), never from this
    /// user's own settings file: DevTools show the user's sign-in token and every response, and call the API with it.
    /// </summary>
    [JsonIgnore]
    public bool AllowDevTools { get; private set; }

    /// <summary>Expected CA fingerprint from the IT policy (setup refuses any other server).</summary>
    [JsonIgnore]
    public string? PolicyCaFingerprint { get; private set; }

    [JsonIgnore]
    public bool ServerFixedByPolicy { get; private set; }

    /// <summary>
    /// Whether HC360 Desktop updates itself from the Server Node (SRS DED-18): yes, unless the IT policy says
    /// <c>"autoUpdate": false</c>, for computers whose software IT distributes itself. The updater reads the same entry.
    /// </summary>
    [JsonIgnore]
    public bool AutoUpdateAllowed { get; private set; } = true;

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

    public static DesktopSettings Load() => Load(SettingsPath, PolicyPath);

    /// <summary>This user's settings from <paramref name="settingsPath"/>, with the machine policy at <paramref name="policyPath"/> over them.</summary>
    internal static DesktopSettings Load(string settingsPath, string policyPath)
    {
        DesktopSettings settings;
        try
        {
            settings = File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(settingsPath), Json) ?? new DesktopSettings()
                : new DesktopSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Settings could not be read; starting with defaults");
            settings = new DesktopSettings();
        }

        settings.ApplyPolicy(policyPath);
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

    /// <summary>
    /// Takes <paramref name="step"/> of the hospital CA's rollover and saves it with <paramref name="save"/> (<see cref="Save"/>):
    /// holds the next CA the server prepared, or pins it once the server switched to it, keeping the CA it replaced. When the
    /// settings cannot be saved they stay as they were, and the exception is thrown: the next check takes the step again.
    /// </summary>
    public void Take(HospitalCaStep step, Action save)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(step);
        var before = (CaCertificatePem, CaFingerprint, NextCaCertificatePem, NextCaCrossCertificatePem, PreviousCaCertificatePem, CaCrossCertificatePem);
        switch (step)
        {
            case HospitalCaStep.HoldNext hold:
                (NextCaCertificatePem, NextCaCrossCertificatePem) = (hold.Next.CaPem, hold.Next.CrossPem);
                break;
            case HospitalCaStep.SwitchToNext when NextCaCertificatePem is { } next:
                (PreviousCaCertificatePem, CaCrossCertificatePem) = (CaCertificatePem, NextCaCrossCertificatePem);
                (CaCertificatePem, CaFingerprint) = (next, Fingerprint(next));
                (NextCaCertificatePem, NextCaCrossCertificatePem) = (null, null);
                break;
            default:
                return;
        }

        try
        {
            save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (CaCertificatePem, CaFingerprint, NextCaCertificatePem, NextCaCrossCertificatePem, PreviousCaCertificatePem, CaCrossCertificatePem) = before;
            throw;
        }
    }

    /// <summary>Forgets the CA pinned and every second pin: trust in the server must be established again.</summary>
    public void ForgetCa()
    {
        (CaCertificatePem, CaFingerprint) = (null, null);
        ForgetRollover();
    }

    /// <summary>Forgets the next and previous CAs: they belong to the CA pinned until now, which setup replaced.</summary>
    public void ForgetRollover()
    {
        (NextCaCertificatePem, NextCaCrossCertificatePem) = (null, null);
        (PreviousCaCertificatePem, CaCrossCertificatePem) = (null, null);
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDirectory);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, SettingsPath, overwrite: true);
    }

    private void ApplyPolicy(string policyPath)
    {
        if (!File.Exists(policyPath))
        {
            return;
        }

        try
        {
            var policy = JsonSerializer.Deserialize<DesktopPolicy>(File.ReadAllText(policyPath), Json);
            if (policy is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(policy.ServerUrl))
            {
                if (!string.Equals(ServerUrl, policy.ServerUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // A different server than the one configured: trust must be established again.
                    ForgetCa();
                }

                ServerUrl = policy.ServerUrl;
                ServerFixedByPolicy = true;
            }

            PolicyCaFingerprint = policy.CaFingerprint?.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            if (PolicyCaFingerprint is not null && CaCertificatePem is not null && !KeepsPolicyCa(PolicyCaFingerprint))
            {
                Log.Warning("The pinned CA does not match the IT policy; setup is required");
                ForgetCa();
            }

            if (policy.Kiosk is { } kiosk)
            {
                Kiosk = kiosk;
            }

            if (!string.IsNullOrWhiteSpace(policy.StartPath) && DeepLinks.IsSafePath(policy.StartPath))
            {
                StartPath = policy.StartPath;
            }

            // disableDevTools is what the installer writes for kiosks and TV boards; it wins over an allowDevTools left beside it.
            AllowDevTools = policy.AllowDevTools == true && policy.DisableDevTools != true;

            if (policy.AutoUpdate == false)
            {
                AutoUpdateAllowed = false;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(ex, "The machine policy could not be read");
        }
    }

    /// <summary>
    /// Whether the workstation keeps a CA the IT policy names (<see cref="HospitalCaPolicy"/>): the CA pinned, or the one it
    /// replaced in a switch this workstation followed, on that CA's word; or the next CA it holds, which it then pins alone.
    /// </summary>
    private bool KeepsPolicyCa(string policyFingerprint)
    {
        switch (HospitalCaPolicy.Decide(policyFingerprint, CaCertificatePem!, PreviousCaCertificatePem, CaCrossCertificatePem, NextCaCertificatePem))
        {
            case HospitalCaPolicyOutcome.Keep:
                return true;
            case HospitalCaPolicyOutcome.PinNextAlone:
                Log.Information("The IT policy names the next hospital CA this workstation holds: it pins that CA alone from now on");
                var next = NextCaCertificatePem;
                ForgetRollover();
                (CaCertificatePem, CaFingerprint) = (next, policyFingerprint);
                return true;
            default:
                return false;
        }
    }

    private static string Fingerprint(string pem)
    {
        using var certificate = X509Certificate2.CreateFromPem(pem);
        return HospitalCaPins.ComputeFingerprint(certificate);
    }

    private sealed record DesktopPolicy(
        string? ServerUrl, string? CaFingerprint, bool? Kiosk, string? StartPath, bool? AllowDevTools, bool? DisableDevTools, bool? AutoUpdate);
}
