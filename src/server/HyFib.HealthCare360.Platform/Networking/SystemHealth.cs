using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.WindowsInterop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Networking;

public sealed record ServerInfo(string MachineName, string OsDescription, string Version, string Environment, string Role, DateTimeOffset StartedAt,
    double UptimeHours, int ProcessorCount, long WorkingSetBytes, string Framework);

public sealed record NetworkInfo(bool WifiSupported, WifiConnectionInfo? Wifi, DateTimeOffset? ConnectedSince, DateTimeOffset? LastDisconnectAt,
    IReadOnlyList<WifiEvent> RecentEvents, IReadOnlyList<string> IpAddresses, int HttpsPort, int PortalHttpsPort, bool MdnsEnabled);

public sealed record DatabaseInfo(bool Reachable, string? Version, long? SizeBytes, int? Connections, string? LastMigration, string? Error);

/// <summary>
/// The data drive, and on Windows what the last BitLocker check recorded (<see cref="DiskEncryption"/>): null when it has
/// never run, and always null on other systems.
/// </summary>
public sealed record StorageInfo(string DataDirectory, long? FreeBytes, long? TotalBytes, DiskEncryptionReport? Encryption = null);

public sealed record BackupInfo(bool Enabled, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, string? LastError, long? LastSizeBytes);

public sealed record OutboxInfo(int Pending, int FailedLast24Hours, DateTimeOffset? OldestPendingAt);

/// <summary>
/// The hospital CA and the server certificate it issued. <see cref="CaExpiresAt"/> is null with an administrator-supplied
/// certificate, whose issuer HC360 does not hold.
/// </summary>
public sealed record CertificateInfo(string? CaFingerprint, DateTimeOffset? ServerExpiresAt, IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset? CaExpiresAt = null);

public sealed record PowerInfo(bool Available, bool? OnBattery, int? ChargePercent, string? Source);

/// <summary>
/// A warning on System health. With several servers (SCAL-01), <see cref="Server"/> names the server a warning about one
/// machine is about (its disk, network, certificate, power, the programs it uses); it is null for the hospital's own (the
/// database, backups, messages, integrations, keys), and on a single Server Node.
/// </summary>
public sealed record HealthWarning(string Code, string Severity, string Message, string? Server = null);

/// <summary>
/// Warnings a module adds to System health about its own set-up, which Platform cannot see (it references no module):
/// the Clinical module's DSC trust folder, for one. Each is asked on every report; it should be quick.
/// </summary>
public interface IHealthWarningSource
{
    Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One server of several (SCAL-01) on System health: what it reports of its own machine (the computer, its network, data
/// drive, certificate and power) and the warnings about them, and when it last did, by the database's clock. Each writes
/// it every minute (<see cref="ServerNodeSharing"/>). <see cref="Reporting"/> is false once it has not for
/// <see cref="ServerNodeBoard.Lifetime"/>: it stopped without saying so, as in a power cut, or cannot reach the database.
/// </summary>
public sealed record ServerNodeHealth(
    ServerInfo Server,
    NetworkInfo Network,
    StorageInfo Storage,
    CertificateInfo Certificate,
    PowerInfo Power,
    IReadOnlyList<HealthWarning> Warnings,
    DateTimeOffset ReportedAt,
    bool Reporting = true);

/// <summary>
/// The report. <see cref="Server"/>, <see cref="Network"/>, <see cref="Storage"/>, <see cref="Certificate"/> and
/// <see cref="Power"/> are those of the server that answered. With several servers (SCAL-01), <see cref="Servers"/> has
/// every one, the node that runs the jobs among them, and the warnings name the server they are about; it is null on a
/// single Server Node.
/// </summary>
public sealed record SystemHealthReport(
    ServerInfo Server,
    NetworkInfo Network,
    DatabaseInfo Database,
    StorageInfo Storage,
    BackupInfo Backup,
    OutboxInfo Outbox,
    CertificateInfo Certificate,
    PowerInfo Power,
    IReadOnlyList<IntegrationHealth> Integrations,
    IReadOnlyList<HealthWarning> Warnings,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ServerNodeHealth>? Servers = null);

public sealed record SystemInfoDto(
    string Product, string Version, string ServerName, string? FacilityName, string? FacilityCode, string? CaFingerprint, DateTimeOffset ServerTime, int ApiVersion,
    SystemFeatures Features);

/// <summary>The hospital-wide feature switches (Platform/Features), for the portal and the sign-in page before anyone signs in.</summary>
public sealed record SystemFeatures(bool PatientPortal, bool Telehealth);

public sealed record WifiSampleDto(DateTimeOffset At, bool Connected, int SignalQuality, int? RssiDbm, double? ReceiveRateMbps, string? Ssid);

/// <summary>
/// Builds the Server Node health report shown on the administrator dashboard (DED-17). With several servers (SCAL-01) the
/// server that answers is always an API node, and a different one from one request to the next behind the reverse proxy:
/// each server writes what it reports of its own machine every minute (<see cref="ServerNodeBoard"/>), and the report has
/// every one's, the node that runs the jobs among them.
/// </summary>
public sealed class SystemHealthService(
    HospitalDbContext db,
    WifiLinkState wifi,
    LanCertificateManager certificates,
    DataDirectories directories,
    IOptions<PlatformOptions> options,
    IHostEnvironment environment,
    IPdfRenderer pdf,
    SigningKeyStore signingKey,
    IFieldKeyRing dataKeys,
    IIntegrationHealthSource integrationHealth,
    IEnumerable<IHealthWarningSource> moduleWarnings,
    ServerNodeBoard servers,
    TimeProvider time)
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    /// <summary>How long a key is used before System health suggests rotating it (the key policy, SECURITY.md).</summary>
    public static readonly TimeSpan SigningKeyPeriod = TimeSpan.FromDays(365);

    public static readonly TimeSpan DataKeyPeriod = TimeSpan.FromDays(2 * 365);

    /// <summary>How long before a certificate expires System health starts to say so: the server certificate and the CA alike.</summary>
    public static readonly TimeSpan CertificateWarningPeriod = TimeSpan.FromDays(30);

    public async Task<SystemHealthReport> BuildAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var own = await ReadThisServerAsync(now, ct);
        var database = await ReadDatabaseAsync(ct);
        var backup = await ReadBackupAsync(ct);

        var dayAgo = now.AddDays(-1);
        var outbox = new OutboxInfo(
            await db.Set<OutboxMessage>().CountAsync(m => m.Status == OutboxStatus.Pending, ct),
            // When it failed, not when it was queued: a message is retried for up to its lifetime (days) before
            // it fails.
            await db.Set<OutboxMessage>()
                .CountAsync(m => m.Status == OutboxStatus.Failed && (m.ProcessedAt ?? m.CreatedAt) > dayAgo, ct),
            await db.Set<OutboxMessage>().Where(m => m.Status == OutboxStatus.Pending).OrderBy(m => m.CreatedAt).Select(m => (DateTimeOffset?)m.CreatedAt).FirstOrDefaultAsync(ct));

        var integrations = await integrationHealth.ListAsync(ct);

        // The hospital's own warnings, about what every server shares.
        var databaseWarnings = new List<HealthWarning>();
        if (!database.Reachable)
        {
            databaseWarnings.Add(new("db.unreachable", "critical", "The database is not reachable."));
        }
        else if (await ReadReplicationAsync(ct) is { } replication)
        {
            // A warm standby and WAL archiving are optional (SRS DED-20, REL-02): nothing shows until they are set up.
            databaseWarnings.AddRange(StandbyReplication.Warnings(replication));
        }

        var backupWarnings = new List<HealthWarning>();
        if (backup.Enabled && (backup.LastSuccessAt is null || now - backup.LastSuccessAt > TimeSpan.FromHours(36)))
        {
            backupWarnings.Add(new("backup.stale", "critical", "No successful backup in the last 36 hours."));
        }

        var messageWarnings = new List<HealthWarning>();
        if (outbox.FailedLast24Hours > 0)
        {
            messageWarnings.Add(new("outbox.failed", "warning", $"{outbox.FailedLast24Hours} outbound message(s) failed in the last 24 hours."));
        }

        if (outbox.OldestPendingAt is { } oldest && now - oldest > TimeSpan.FromMinutes(30))
        {
            messageWarnings.Add(new("outbox.backlog", "warning", "Outbound messages are queued for more than 30 minutes (internet down?)."));
        }

        messageWarnings.AddRange(IntegrationWarnings(integrations));

        var keyWarnings = new List<HealthWarning>();
        if (signingKey.KeyCreatedAt is { } signedSince && now - signedSince > SigningKeyPeriod)
        {
            keyWarnings.Add(new("keys.signing_due", "info", "The sign-in token key is over a year old. Rotate it under Encryption keys."));
        }

        if (dataKeys.Keys.Single(k => k.Current).CreatedAt is { } dataSince && now - dataSince > DataKeyPeriod)
        {
            keyWarnings.Add(new("keys.data_due", "info", "The data encryption key is over two years old. Rotate it under Encryption keys."));
        }

        // Backups still use the original recovery key. Until HC360 made separate recovery keys, any holder of the backups
        // permission could display it; one never displayed is held by nobody.
        if (!File.Exists(RecoveryKeyStore.PathIn(directories)))
        {
            keyWarnings.Add(await RecoveryKeyService.OriginalWasDisplayedAsync(db, ct)
                ? new("backup.recovery_key_original", "warning",
                    "Backups still use the original recovery key, which any administrator with the backups permission could display. Make a new one under Backups › Recovery key.")
                : new("backup.recovery_key_missing", "critical",
                    "Nobody holds the recovery key for the backups, so they cannot be restored on new hardware. Make one under Backups › Recovery key."));
        }

        // A single Server Node: its own warnings in the places System health has always shown them.
        if (!directories.IsShared)
        {
            List<HealthWarning> warnings = [.. databaseWarnings, .. own.Machine, .. backupWarnings, .. own.Link, .. messageWarnings, .. own.Services, .. keyWarnings];
            return new SystemHealthReport(own.Server, own.Network, database, own.Storage, backup, outbox, own.Certificate, own.Power, integrations, warnings, now);
        }

        // Several servers: every one's, after the hospital's.
        var everyServer = await EveryServerAsync(own.ToNode(now), ct);
        List<HealthWarning> all = [.. databaseWarnings, .. backupWarnings, .. messageWarnings, .. keyWarnings, .. ServerWarnings(everyServer)];
        return new SystemHealthReport(own.Server, own.Network, database, own.Storage, backup, outbox, own.Certificate, own.Power, integrations, all, now, everyServer);
    }

    /// <summary>What this server reports of itself to the other servers of several (<see cref="ServerNodeSharing"/>).</summary>
    public async Task<ServerNodeHealth> BuildServerAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return (await ReadThisServerAsync(now, ct)).ToNode(now);
    }

    /// <summary>
    /// The warnings about every server, each naming it: its own while it reports, and that it has stopped when it has not;
    /// when none of them runs the jobs, that nothing runs them; and when more than one that reports does, that one should.
    /// </summary>
    internal static IReadOnlyList<HealthWarning> ServerWarnings(IReadOnlyList<ServerNodeHealth> servers)
    {
        var warnings = new List<HealthWarning>();
        foreach (var server in servers)
        {
            var name = server.Server.MachineName;
            if (server.Reporting)
            {
                warnings.AddRange(server.Warnings.Select(w => w with { Server = name }));
            }
            else
            {
                warnings.Add(RunsJobs(server)
                    ? new("server.not_reporting", "critical",
                        $"{name}, which runs the background jobs, has not reported for more than three minutes: it may be off, its HC360Server service stopped, or the database out of its reach. Until it is back, nothing makes the backups, sends messages or listens for analysers and modalities.",
                        name)
                    : new("server.not_reporting", "warning",
                        $"{name} has not reported for more than three minutes: it may be off, its HC360Server service stopped, or the database out of its reach. The reverse proxy sends requests to the other servers meanwhile.",
                        name));
            }
        }

        if (!servers.Any(RunsJobs))
        {
            warnings.Add(new("server.no_jobs", "critical",
                "No server that runs the background jobs is reporting: nothing makes the backups, sends messages or listens for analysers and modalities. Start HC360 on the server that runs them."));
        }

        // Exactly one should (DEPLOYMENT.md section 10); an API node whose settings miss "Role": "api" runs them as "all".
        // Nothing shares the nightly backup out between them, so each makes it: a warning, as nothing is lost meanwhile.
        var running = servers.Where(s => s.Reporting && RunsJobs(s)).Select(s => s.Server.MachineName).ToArray();
        if (running.Length > 1)
        {
            warnings.Add(new("server.several_jobs", "warning",
                $"{string.Join(", ", running[..^1])} and {running[^1]} {(running.Length == 2 ? "both" : "all")} run the background jobs, and only one server should: each makes the nightly backup, and listens for analysers and modalities. Give all but one of them \"Role\": \"api\" in hc360.settings.json, and restart their HC360Server service."));
        }

        return warnings;
    }

    /// <summary>This server and every other one that reports or stopped reporting less than a day ago, the nodes that run the jobs first.</summary>
    private async Task<IReadOnlyList<ServerNodeHealth>> EveryServerAsync(ServerNodeHealth self, CancellationToken ct) =>
    [
        .. (await servers.ReadOthersAsync(ct)).Prepend(self)
            .OrderBy(s => RunsJobs(s) ? 0 : 1)
            .ThenBy(s => s.Server.MachineName, StringComparer.OrdinalIgnoreCase),
    ];

    private static bool RunsJobs(ServerNodeHealth server) => !string.Equals(server.Server.Role, "api", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// This server's own part of the report: its machine and the warnings about it, in the three places System health has
    /// always given them on a single Server Node (after the database's, after the backups', after the messages').
    /// </summary>
    private async Task<ThisServer> ReadThisServerAsync(DateTimeOffset now, CancellationToken ct)
    {
        var process = Process.GetCurrentProcess();
        var server = new ServerInfo(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            ProductVersion,
            environment.EnvironmentName,
            options.Value.Role,
            StartedAt,
            Math.Round((now - StartedAt).TotalHours, 2),
            Environment.ProcessorCount,
            process.WorkingSet64,
            RuntimeInformation.FrameworkDescription);

        var network = new NetworkInfo(
            wifi.Supported, wifi.Current, wifi.ConnectedSince, wifi.LastDisconnectAt, wifi.RecentEvents,
            LanCertificateManager.CurrentAddresses().Select(a => a.ToString()).ToList(),
            options.Value.HttpsPort, options.Value.PortalHttpsPort, options.Value.Network.EnableMdns);

        var encryption = OperatingSystem.IsWindows() ? DiskEncryption.Load(DiskEncryption.PathIn(directories)) : null;
        var storage = ReadStorage() with { Encryption = encryption };
        var certificate = new CertificateInfo(certificates.CaFingerprint, certificates.ServerExpiresAt, certificates.SubjectAlternativeNames,
            certificates.CaExpiresAt);
        var power = ReadPower();

        // The machine: its data drive, and BitLocker, the settings file's secrets and the database watchdog, which exist only
        // on the Windows Server Node (SRS SEC-05, SEC-07, DED-01).
        var machine = new List<HealthWarning>();
        if (storage.FreeBytes is { } free && storage.TotalBytes is { } total && total > 0 && free < total / 10)
        {
            machine.Add(new("disk.low", "critical", $"Less than 10% disk space free on the data drive ({free / (1024 * 1024 * 1024)} GB)."));
        }

        if (OperatingSystem.IsWindows())
        {
            machine.AddRange(DiskEncryption.Warnings(encryption, now));
            machine.AddRange(DatabaseWatchdog.Warnings(DatabaseWatchdog.Load(DatabaseWatchdog.PathIn(directories)), now));
            if (PlainSecrets() is { Count: > 0 } plain)
            {
                machine.Add(new("settings.unprotected", "warning",
                    $"The settings file holds {(plain.Count == 1 ? "a secret" : $"{plain.Count} secrets")} in plain text ({string.Join(", ", plain)}). " +
                    "Run Install-HC360Server.ps1 again, or \"HyFib.HealthCare360.Server.exe settings protect\" from an elevated prompt, to protect them with DPAPI."));
            }
        }

        // Its link: its certificate, and its WiFi.
        var link = new List<HealthWarning>(CertificateWarnings(certificate, now));
        if (network.Wifi is { IsConnected: true, DetailsAvailable: false } hidden)
        {
            link.Add(new("wifi.details_hidden", "info", hidden.Note ?? WifiConnectionInfo.LocationPermissionNote));
        }
        else if (network.Wifi is { IsConnected: true } connected)
        {
            if (connected.SignalQuality < options.Value.Network.WifiWeakSignalThreshold)
            {
                link.Add(new("wifi.weak", "warning", $"WiFi signal is weak ({connected.SignalQuality}%)."));
            }

            if (connected.IsInsecure)
            {
                link.Add(new("wifi.insecure", "critical", "The Server Node is on an insecure WiFi network."));
            }
            else if (connected.UsesSharedPassword)
            {
                link.Add(new("wifi.shared_password", "warning", "The Server Node is on a WiFi network with a shared password (WPA-Personal). Use WPA2/WPA3-Enterprise."));
            }
        }
        else if (network.WifiSupported && network.Wifi is { IsConnected: false })
        {
            link.Add(new("wifi.down", "critical", "The Server Node's WiFi is disconnected."));
        }

        // What runs on it: the modules' own set-up here (such as the DSC trust folder), the PDF browser, and its UPS.
        var services = new List<HealthWarning>();
        foreach (var source in moduleWarnings)
        {
            services.AddRange(await source.WarningsAsync(ct));
        }

        // Reports, prescriptions and bills are printed and sent as server-made documents: say so when that is broken.
        if (pdf.Status is { Available: false } pdfStatus)
        {
            services.Add(new("pdf.unavailable", "warning", $"PDFs can't be made on this server: {pdfStatus.Problem}"));
        }

        if (power is { Available: true, OnBattery: true })
        {
            services.Add(new("power.battery", "critical", $"Running on UPS/battery power ({power.ChargePercent}% remaining)."));
        }

        return new ThisServer(server, network, storage, certificate, power, machine, link, services);
    }

    /// <summary>
    /// The server certificate is issued again by itself before it expires, so a warning about it means that failed. The
    /// hospital CA is never made again by itself: every workstation, tablet and HC360 Desktop trusts it, so replacing it
    /// needs planning, and once it has expired nothing trusts the server.
    /// </summary>
    public static IReadOnlyList<HealthWarning> CertificateWarnings(CertificateInfo certificate, DateTimeOffset now)
    {
        var warnings = new List<HealthWarning>();
        if (certificate.ServerExpiresAt is { } expires && expires - now < CertificateWarningPeriod)
        {
            warnings.Add(new("cert.expiring", "warning", $"The server certificate expires on {expires:dd MMM yyyy}."));
        }

        if (certificate.CaExpiresAt is { } caExpires && caExpires <= now)
        {
            warnings.Add(new("cert.ca_expired", "critical",
                $"The hospital CA expired on {caExpires:dd MMM yyyy}: workstations, tablets and HC360 Desktop no longer trust this server. Contact HyFib support."));
        }
        else if (certificate.CaExpiresAt is { } caExpiring && caExpiring - now < CertificateWarningPeriod)
        {
            warnings.Add(new("cert.ca_expiring", "warning",
                $"The hospital CA expires on {caExpiring:dd MMM yyyy}. Workstations, tablets and HC360 Desktop all trust it, so plan replacing it with HyFib support now."));
        }

        return warnings;
    }

    /// <summary>
    /// One warning naming every integration that is switched on and whose latest call failed, as the integrations
    /// dashboard says. Those that are off, or failed earlier but work again, are left to that dashboard.
    /// </summary>
    public static IReadOnlyList<HealthWarning> IntegrationWarnings(IReadOnlyList<IntegrationHealth> integrations)
    {
        var failing = integrations.Where(i => i.Enabled && i.Health == IntegrationHealth.Failing).Select(i => i.DisplayName).ToList();
        return failing.Count == 0
            ? []
            : [new("integrations.failing", "warning",
                $"{string.Join(", ", failing)} {(failing.Count == 1 ? "is" : "are")} failing: the latest call failed. See Administration › Integrations.")];
    }

    public static string ProductVersion =>
        typeof(SystemHealthService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    private async Task<DatabaseInfo> ReadDatabaseAsync(CancellationToken ct)
    {
        try
        {
            var version = await db.Database.SqlQuery<string>($"SELECT version() AS \"Value\"").SingleAsync(ct);
            var size = await db.Database.SqlQuery<long>($"SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(ct);
            var connections = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database()").SingleAsync(ct);
            var migrations = await db.Database.GetAppliedMigrationsAsync(ct);
            return new DatabaseInfo(true, version.Split(',')[0], size, connections, migrations.LastOrDefault(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DatabaseInfo(false, null, null, null, null, ex.Message);
        }
    }

    /// <summary>The warm standby's replication slot and the WAL archiver; null when PostgreSQL could not be asked.</summary>
    private async Task<ReplicationState?> ReadReplicationAsync(CancellationToken ct)
    {
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                return await StandbyReplication.ReadAsync(db.Database.GetDbConnection(), ct);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            return null;
        }
    }

    private IReadOnlyList<string> PlainSecrets()
    {
        try
        {
            return ProtectedSettings.PlainSecrets(ProtectedSettings.PathIn(directories));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private StorageInfo ReadStorage()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directories.Root));
            if (root is null)
            {
                return new StorageInfo(directories.Root, null, null);
            }

            var drive = new DriveInfo(root);
            return new StorageInfo(directories.Root, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new StorageInfo(directories.Root, null, null);
        }
    }

    private async Task<BackupInfo> ReadBackupAsync(CancellationToken ct)
    {
        var lastSuccess = await db.Set<BackupRun>().Where(r => r.Status == BackupStatus.Succeeded).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
        var lastFailure = await db.Set<BackupRun>().Where(r => r.Status == BackupStatus.Failed).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
        return new BackupInfo(options.Value.Backup.Enabled, lastSuccess?.CompletedAt, lastFailure?.CompletedAt, lastFailure?.Error, lastSuccess?.SizeBytes);
    }

    private static PowerInfo ReadPower()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PowerInfo(false, null, null, null);
        }

        try
        {
            return ReadWindowsPower();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or PlatformNotSupportedException)
        {
            return new PowerInfo(false, null, null, null);
        }
    }

    [SupportedOSPlatform("windows")]
    private static PowerInfo ReadWindowsPower()
    {
        using var searcher = new ManagementObjectSearcher("SELECT BatteryStatus, EstimatedChargeRemaining, Name FROM Win32_Battery");
        foreach (var item in searcher.Get())
        {
            using var battery = (ManagementObject)item;
            var status = Convert.ToInt32(battery["BatteryStatus"], System.Globalization.CultureInfo.InvariantCulture);
            var charge = battery["EstimatedChargeRemaining"] is { } c ? Convert.ToInt32(c, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
            // 1 = discharging (running on battery/UPS); 2 = on AC power.
            return new PowerInfo(true, status == 1, charge, battery["Name"]?.ToString());
        }

        return new PowerInfo(false, null, null, null);
    }

    /// <summary>This server's machine, and the warnings about it in its three places in the report.</summary>
    private sealed record ThisServer(
        ServerInfo Server,
        NetworkInfo Network,
        StorageInfo Storage,
        CertificateInfo Certificate,
        PowerInfo Power,
        IReadOnlyList<HealthWarning> Machine,
        IReadOnlyList<HealthWarning> Link,
        IReadOnlyList<HealthWarning> Services)
    {
        public ServerNodeHealth ToNode(DateTimeOffset at) => new(Server, Network, Storage, Certificate, Power, [.. Machine, .. Link, .. Services], at);
    }
}

internal static class SystemEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var system = app.MapGroup("/api/v1/system").WithTags("System");

        // Used by HC360 Desktop during discovery/enrolment to confirm it has found an HC360 server, and by the sign-in page
        // and the patient portal, which also learn from it which features are switched on.
        system.MapGet("/info", async (HospitalDbContext db, LanCertificateManager certificates, IFeatureFlags features, TimeProvider time, CancellationToken ct) =>
            {
                var facility = await db.Set<Facility>().AsNoTracking().OrderBy(f => f.CreatedAt).Select(f => new { f.Name, f.Code }).FirstOrDefaultAsync(ct);
                var switches = new SystemFeatures(
                    await features.IsEnabledAsync(FeatureKeys.PatientPortal, null, ct),
                    await features.IsEnabledAsync(FeatureKeys.Telehealth, null, ct));
                return TypedResults.Ok(new SystemInfoDto("HyFib HealthCare360", SystemHealthService.ProductVersion, Environment.MachineName,
                    facility?.Name, facility?.Code, certificates.CaFingerprint, time.GetUtcNow(), 1, switches));
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public);

        // Public CA certificate for tablets/browsers on the hospital WiFi (contains no secrets).
        system.MapGet("/ca-certificate", (LanCertificateManager certificates) =>
                certificates.CaPem is { } pem
                    ? TypedResults.File(System.Text.Encoding.ASCII.GetBytes(pem), "application/x-x509-ca-cert", "hc360-hospital-ca.crt")
                    : Results.NotFound())
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public);

        var admin = app.MapGroup("/api/v1/admin/system").WithTags("Administration").RequirePermission(PlatformPermissions.SystemRead);

        admin.MapGet("/health", async (SystemHealthService health, CancellationToken ct) => TypedResults.Ok(await health.BuildAsync(ct)));

        // The database password (SRS SEC-07): who may see whether it can be changed, and who may change it.
        admin.MapGet("/database-password", async (DatabasePasswordService passwords, CancellationToken ct) => TypedResults.Ok(await passwords.StatusAsync(ct)));
        admin.MapPost("/database-password/change", async (DatabasePasswordService passwords, CancellationToken ct) => (await passwords.ChangeAsync(ct)).ToOk())
            .RequirePermission(PlatformPermissions.SystemManage);

        // The encryption keys (SRS SEC-07): who may see their age and what is left to move, and who may rotate them, and
        // retire an earlier data key once nothing is encrypted with it (after giving their password again).
        admin.MapGet("/keys", async (KeyRotationService keys, CancellationToken ct) => TypedResults.Ok(await keys.StatusAsync(ct)));
        admin.MapPost("/keys/data/rotate", async (KeyRotationService keys, CancellationToken ct) => TypedResults.Ok(await keys.RotateDataKeyAsync(ct)))
            .RequirePermission(PlatformPermissions.SystemManage);
        admin.MapPost("/keys/data/{keyId}/retire", async (string keyId, ReauthenticateRequest request, KeyRotationService keys, CancellationToken ct) =>
                (await keys.RetireDataKeyAsync(keyId, request, ct)).ToOk())
            .RequirePermission(PlatformPermissions.SystemManage)
            .RequireAddressRateLimit(ClusterRateLimits.SignIn);
        admin.MapPost("/keys/signing/rotate", async (KeyRotationService keys, CancellationToken ct) => (await keys.RotateSigningKeyAsync(ct)).ToOk())
            .RequirePermission(PlatformPermissions.SystemManage);

        admin.MapGet("/wifi-samples", async (int? hours, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var since = time.GetUtcNow().AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 30));
            return TypedResults.Ok(await db.Set<WifiSample>().AsNoTracking()
                .Where(s => s.At >= since)
                .OrderBy(s => s.At)
                .Select(s => new WifiSampleDto(s.At, s.Connected, s.SignalQuality, s.RssiDbm, s.ReceiveRateMbps, s.Ssid))
                .ToListAsync(ct));
        });
    }
}
