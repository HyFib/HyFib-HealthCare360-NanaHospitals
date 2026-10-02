namespace HyFib.HealthCare360.Platform.Hosting;

/// <summary>Root configuration section <c>HealthCare360</c> for the Server Node.</summary>
public sealed class PlatformOptions
{
    public const string SectionName = "HealthCare360";

    /// <summary>Data root. Default: %ProgramData%\HyFib\HealthCare360 on Windows, ./hc360-data elsewhere.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>HTTPS port served on the hospital LAN/WiFi.</summary>
    public int HttpsPort { get; set; } = 7360;

    /// <summary>Optional plain-HTTP port used only to redirect to HTTPS (0 = disabled).</summary>
    public int HttpRedirectPort { get; set; }

    /// <summary>Default facility code used on first start (UHID prefix etc.).</summary>
    public string DefaultFacilityCode { get; set; } = "NH";

    public string DefaultFacilityName { get; set; } = "Nana Hospitals";

    public string TimeZone { get; set; } = "Asia/Kolkata";

    /// <summary>
    /// Apply EF Core migrations automatically at start-up, after a verified backup of the existing database
    /// (<see cref="BackupOptions.BeforeMigrations"/>).
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>Seed demonstration practitioners/patients (never enable in production).</summary>
    public bool SeedDemoData { get; set; }

    /// <summary>Process role: <c>all</c> (default), <c>api</c> (no background jobs) or <c>worker</c> (jobs only).</summary>
    public string Role { get; set; } = "all";

    /// <summary>Serve the interactive API reference at /docs.</summary>
    public bool EnableApiReference { get; set; }

    public SecurityOptions Security { get; set; } = new();

    public NetworkOptions Network { get; set; } = new();

    public BackupOptions Backup { get; set; } = new();

    public PdfOptions Pdf { get; set; } = new();

    public bool RunsJobs => !string.Equals(Role, "api", StringComparison.OrdinalIgnoreCase);

    public bool ServesApi => !string.Equals(Role, "worker", StringComparison.OrdinalIgnoreCase);
}

public sealed class SecurityOptions
{
    public string Issuer { get; set; } = "hc360";
    public string Audience { get; set; } = "hc360";
    public int AccessTokenMinutes { get; set; } = 10;

    /// <summary>Absolute session length — one clinical shift.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>Refresh must happen within this idle window or the session ends.</summary>
    public int SessionIdleMinutes { get; set; } = 120;

    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int MinPasswordLength { get; set; } = 12;
    public int PasswordHashIterations { get; set; } = 210_000;

    /// <summary>Enforce TOTP for <see cref="MfaRequiredRoles"/> (disable only on development machines).</summary>
    public bool EnforceMfa { get; set; } = true;

    /// <summary>Roles that must use TOTP multi-factor authentication.</summary>
    public string[] MfaRequiredRoles { get; set; } = ["SuperAdmin", "HospitalAdmin"];

    /// <summary>Roles that may sign in only from enrolled HC360 Desktop devices.</summary>
    public string[] EnrolledDeviceRequiredRoles { get; set; } = [];

    public int MaxPinAttempts { get; set; } = 5;
}

public sealed class NetworkOptions
{
    public bool EnableMdns { get; set; } = true;
    public string MdnsServiceType { get; set; } = "_hc360._tcp";

    public bool EnableWifiMonitor { get; set; } = true;
    public int WifiSampleSeconds { get; set; } = 30;

    /// <summary>Signal quality (0–100) below which the link is considered degraded.</summary>
    public int WifiWeakSignalThreshold { get; set; } = 40;

    /// <summary>Approved staff SSIDs; connecting elsewhere raises a security warning (empty = no check).</summary>
    public string[] ApprovedSsids { get; set; } = [];

    /// <summary>Generate a private hospital CA + server certificate on first start.</summary>
    public bool GenerateLanCertificate { get; set; } = true;

    /// <summary>Administrator-supplied PFX (overrides the generated certificate).</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    /// <summary>Extra DNS names to include in the generated server certificate.</summary>
    public string[] AdditionalHostNames { get; set; } = [];
}

public sealed class BackupOptions
{
    /// <summary>The nightly backup (<see cref="Schedule"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Quartz cron expression (default 01:30 every day).</summary>
    public string Schedule { get; set; } = "0 30 1 * * ?";

    /// <summary>
    /// Back up the database before start-up applies migrations to it, and do not change the database when that backup
    /// fails. Independent of <see cref="Enabled"/>; turn it off only on development machines without pg_dump.
    /// </summary>
    public bool BeforeMigrations { get; set; } = true;

    public string? Directory { get; set; }

    /// <summary>Secondary copy target (USB drive or NAS share).</summary>
    public string? SecondaryDirectory { get; set; }

    public int RetentionDays { get; set; } = 30;

    public string PgDumpPath { get; set; } = "pg_dump";

    /// <summary>pg_restore, used to verify each backup. By default the one next to pg_dump.</summary>
    public string? PgRestorePath { get; set; }
}

/// <summary>
/// Server-side PDFs (ADR-009): documents are laid out in HTML and printed to PDF by headless Microsoft Edge, which
/// every Windows 10/11 and Windows Server 2022 machine has. The browser starts with the first PDF and closes when idle.
/// </summary>
public sealed class PdfOptions
{
    /// <summary>
    /// msedge.exe or chrome.exe. By default Microsoft Edge from its standard install, then Google Chrome. Set this only
    /// when the browser is installed somewhere else.
    /// </summary>
    public string? BrowserPath { get; set; }

    /// <summary>PDFs made at the same time, each in a browser tab of its own (1–8).</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>A PDF that takes longer is abandoned, and the browser restarted if it no longer answers.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Close the browser after this many minutes without a PDF. The next one starts it again (about a second).</summary>
    public int IdleMinutes { get; set; } = 10;

    /// <summary>Start a fresh browser after this many PDFs, which keeps memory in check and applies Edge updates.</summary>
    public int RecycleAfter { get; set; } = 500;
}

/// <summary>Resolved on-disk locations under the data root.</summary>
public sealed class DataDirectories
{
    public DataDirectories(PlatformOptions options)
    {
        Root = ResolveRoot(options.DataDirectory);
        Keys = Path.Combine(Root, "keys");
        Certificates = Path.Combine(Root, "certs");
        Files = Path.Combine(Root, "files");
        Logs = Path.Combine(Root, "logs");
        Backups = string.IsNullOrWhiteSpace(options.Backup.Directory) ? Path.Combine(Root, "backups") : options.Backup.Directory;
        Temp = Path.Combine(Root, "temp");
    }

    public string Root { get; }
    public string Keys { get; }
    public string Certificates { get; }
    public string Files { get; }
    public string Logs { get; }
    public string Backups { get; }
    public string Temp { get; }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Keys, Certificates, Files, Logs, Backups, Temp })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public static string ResolveRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }

        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyFib", "HealthCare360")
            : Path.Combine(AppContext.BaseDirectory, "hc360-data");
    }
}
