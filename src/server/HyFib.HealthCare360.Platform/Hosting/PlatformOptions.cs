namespace HyFib.HealthCare360.Platform.Hosting;

/// <summary>Root configuration section <c>HealthCare360</c> for the Server Node.</summary>
public sealed class PlatformOptions
{
    public const string SectionName = "HealthCare360";

    /// <summary>Data root. Default: %ProgramData%\HyFib\HealthCare360 on Windows, ./hc360-data elsewhere.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>HTTPS port of the staff app, HC360 Desktop and the integrations, served on the hospital LAN/WiFi.</summary>
    public int HttpsPort { get; set; } = 7360;

    /// <summary>
    /// HTTPS port of the patient portal: an address of its own, so script on a portal page, such as the payment checkout,
    /// cannot reach the staff app or a staff session (ASVS V3.5.4, <see cref="Http.ListenerPaths"/>). Portal links to the
    /// staff port are sent here.
    /// </summary>
    public int PortalHttpsPort { get; set; } = 7361;

    /// <summary>
    /// HTTPS port of the interactive API reference while it is on (<see cref="EnableApiReference"/>): an address of its own,
    /// on the server machine only, that takes and sets no cookies.
    /// </summary>
    public int ApiReferencePort { get; set; } = 7362;

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

    /// <summary>
    /// Serve the interactive API reference at /docs on <see cref="ApiReferencePort"/> (always on in development).
    /// </summary>
    public bool EnableApiReference { get; set; }

    public SecurityOptions Security { get; set; } = new();

    public NetworkOptions Network { get; set; } = new();

    public BackupOptions Backup { get; set; } = new();

    public PdfOptions Pdf { get; set; } = new();

    public ClusterOptions Cluster { get; set; } = new();

    public PartitionOptions Partitions { get; set; } = new();

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

    /// <summary>
    /// A staff session ends after this long with nobody using the app. Its screen locks long before, after the
    /// <c>ui.idleLockMinutes</c> setting (10 minutes by default).
    /// </summary>
    public int SessionIdleMinutes { get; set; } = 120;

    /// <summary>A patient portal session ends after this long with nobody using it: portals run on shared phones.</summary>
    public int PortalSessionIdleMinutes { get; set; } = 20;

    /// <summary>
    /// Wrong passwords and wrong authenticator codes in a row before an account locks for <see cref="LockoutMinutes"/>;
    /// each further run of this many doubles the lock, up to 24 hours.
    /// </summary>
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>
    /// Failures are forgotten after this long without another, so the occasional mistyped password does not add up to
    /// ever longer locks.
    /// </summary>
    public int FailedLoginResetHours { get; set; } = 24;

    /// <summary>How long a temporary password an administrator gives out works; the user replaces it at first sign-in.</summary>
    public int TemporaryPasswordHours { get; set; } = 72;

    public int MinPasswordLength { get; set; } = 12;
    public int PasswordHashIterations { get; set; } = 210_000;

    /// <summary>
    /// Enforce TOTP for <see cref="MfaRequiredRoles"/> and, with <see cref="MfaForAdministration"/>, for administrators
    /// (disable only on development machines).
    /// </summary>
    public bool EnforceMfa { get; set; } = true;

    /// <summary>
    /// Roles that must use TOTP multi-factor authentication: the administrators, and those who handle accounts, payroll
    /// and staff bank details.
    /// </summary>
    public string[] MfaRequiredRoles { get; set; } = ["SuperAdmin", "HospitalAdmin", "Accountant", "HrManager"];

    /// <summary>
    /// Also require TOTP from anyone holding an administrative permission (<see cref="Security.PlatformPermissions.IsAdministrative"/>),
    /// whatever their roles are called, so a custom role with administrative rights needs it too.
    /// </summary>
    public bool MfaForAdministration { get; set; } = true;

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

    /// <summary>
    /// Addresses of the reverse proxies in front of the server (SCAL-01), such as <c>10.0.0.5</c>. A request from one of
    /// them is taken to come from the client its <c>X-Forwarded-For</c> header names, and over the scheme its
    /// <c>X-Forwarded-Proto</c> names, so per-address rate limits, the audit trail, device and webhook address checks see
    /// the real client. Empty (the default): the headers are ignored, so nobody can claim another address by sending them.
    /// </summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Networks the reverse proxies are on, in CIDR form (<c>10.0.0.0/29</c>), as for <see cref="KnownProxies"/>.</summary>
    public string[] KnownNetworks { get; set; } = [];

    public bool BehindReverseProxy => KnownProxies.Length > 0 || KnownNetworks.Length > 0;
}

/// <summary>
/// Several HC360 servers on one database (SCAL-01): API nodes behind a reverse proxy and one node that runs the jobs
/// (<c>--role</c>). Any node may answer any request, so what a request may need from a file is kept in
/// <see cref="SharedDirectory"/>, the same folder for every node.
/// </summary>
public sealed class ClusterOptions
{
    /// <summary>
    /// A folder every node reads and writes, such as a file share (<c>\\hc360-files\hc360</c>): the keys (the data
    /// encryption keys, the sign-in token key, the backup recovery key and the Data Protection key ring that protects them)
    /// and the stored documents are kept here instead of under each node's data directory. Empty (the default) on a single
    /// Server Node.
    /// </summary>
    public string? SharedDirectory { get; set; }

    /// <summary>
    /// Thumbprint of the certificate, with its RSA private key, in the Local Computer's Personal store of every node, that
    /// protects the Data Protection key ring in <see cref="SharedDirectory"/>: machine DPAPI, which protects it on a single
    /// node, cannot be read on another machine.
    /// </summary>
    public string? KeyCertificateThumbprint { get; set; }

    /// <summary>Or a PFX file holding that certificate and its private key, opened with <see cref="KeyCertificatePassword"/>.</summary>
    public string? KeyCertificatePath { get; set; }

    public string? KeyCertificatePassword { get; set; }

    public bool IsShared => !string.IsNullOrWhiteSpace(SharedDirectory);
}

/// <summary>
/// Monthly partitions of the busiest tables (SCAL-04): the audit trail, remote-monitoring readings, the patient and staff
/// message log and the integration log. The node that runs the jobs adds each month's partition ahead of time and, only
/// when a retention is set, drops the partitions that are entirely older than it.
/// </summary>
public sealed class PartitionOptions
{
    /// <summary>How many months ahead partitions are made (1–24, default 3). Rows for a later month go to the default one.</summary>
    public int MonthsAhead { get; set; } = 3;

    /// <summary>
    /// Months to keep, per table (<c>virtualcare.rpm_readings</c>, <c>platform.notification_logs</c>,
    /// <c>integrations.integration_logs</c>): a month's partition is dropped once all of it is older. A table not listed
    /// keeps everything, which is the default for all of them. The audit trail cannot be listed: it is kept for at least
    /// ten years by law (SRS SEC-04) and its hash chain covers every row.
    /// </summary>
    public Dictionary<string, int> RetentionMonths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
        ArgumentNullException.ThrowIfNull(options);
        Cluster = options.Cluster;
        Root = ResolveRoot(options.DataDirectory);
        Shared = Cluster.IsShared ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(Cluster.SharedDirectory!)) : null;
        Config = Path.Combine(Root, "config");
        Keys = Path.Combine(Shared ?? Root, "keys");
        Certificates = Path.Combine(Root, "certs");
        Files = Path.Combine(Shared ?? Root, "files");
        Logs = Path.Combine(Root, "logs");
        Backups = string.IsNullOrWhiteSpace(options.Backup.Directory) ? Path.Combine(Root, "backups") : options.Backup.Directory;
        Temp = Path.Combine(Root, "temp");
    }

    public string Root { get; }

    /// <summary>
    /// The folder every node of several shares (<see cref="ClusterOptions.SharedDirectory"/>), which then holds
    /// <see cref="Keys"/> and <see cref="Files"/>; null on a single Server Node. Each node keeps its own settings, TLS
    /// certificates, logs, backups and temporary files under <see cref="Root"/>.
    /// </summary>
    public string? Shared { get; }

    public bool IsShared => Shared is not null;

    public ClusterOptions Cluster { get; }

    /// <summary>The site's settings (<c>hc360.settings.json</c>) and what the elevated checks record for the server.</summary>
    public string Config { get; }

    public string Keys { get; }
    public string Certificates { get; }
    public string Files { get; }
    public string Logs { get; }
    public string Backups { get; }
    public string Temp { get; }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Config, Keys, Certificates, Files, Logs, Backups, Temp })
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
