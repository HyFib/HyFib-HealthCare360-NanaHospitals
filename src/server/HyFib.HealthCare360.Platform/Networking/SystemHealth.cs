using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HyFib.HealthCare360.Platform.Auditing;
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
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.WindowsInterop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Networking;

/// <param name="Clock">The last check of this server's clock against the national time servers (<see cref="ServerClock"/>); null before the first.</param>
public sealed record ServerInfo(string MachineName, string OsDescription, string Version, string Environment, string Role, DateTimeOffset StartedAt,
    double UptimeHours, int ProcessorCount, long WorkingSetBytes, string Framework, ClockCheck? Clock = null);

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
/// certificate, whose issuer HC360 does not hold. While a next CA is prepared, <see cref="NextCaFingerprint"/> is that CA,
/// which devices take before the switch. After the switch, <see cref="PreviousCaFingerprint"/> is the CA it replaced, which
/// devices keep as a second pin until <see cref="PreviousCaExpiresAt"/>.
/// </summary>
public sealed record CertificateInfo(string? CaFingerprint, DateTimeOffset? ServerExpiresAt, IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset? CaExpiresAt = null, string? PreviousCaFingerprint = null, DateTimeOffset? PreviousCaExpiresAt = null, string? NextCaFingerprint = null);

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

/// <summary>
/// What the server says of itself before anyone signs in. While a next CA is prepared, <see cref="NextCaFingerprint"/> is that
/// CA (handed out with the current CA's word for it at <c>ca-certificate/next</c>). After a switch,
/// <see cref="PreviousCaFingerprint"/> is the CA the current one replaced, until it expires.
/// </summary>
public sealed record SystemInfoDto(
    string Product, string Version, string ServerName, string? FacilityName, string? FacilityCode, string? CaFingerprint, DateTimeOffset ServerTime, int ApiVersion,
    SystemFeatures Features, string? PreviousCaFingerprint = null, string? NextCaFingerprint = null);

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
    ServerClock clock,
    IAdministrationDesktops administrationDesktops,
    ISettingsService settings,
    TimeProvider time)
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    /// <summary>How long a key is used before System health suggests rotating it (the key policy, SECURITY.md).</summary>
    public static readonly TimeSpan SigningKeyPeriod = TimeSpan.FromDays(365);

    public static readonly TimeSpan DataKeyPeriod = TimeSpan.FromDays(2 * 365);

    /// <summary>How long before the server certificate expires System health starts to say so.</summary>
    public static readonly TimeSpan CertificateWarningPeriod = TimeSpan.FromDays(30);

    /// <summary>
    /// The server certificate is issued again by itself 30 days before it expires (or, when an administrator supplied it,
    /// replaced by them): one with less than this left was not, and the warning turns critical, which is told to IT on-call.
    /// </summary>
    public static readonly TimeSpan CertificateUrgentPeriod = TimeSpan.FromDays(14);

    /// <summary>
    /// How long before the hospital CA expires System health starts to say so. Every HC360 Desktop, lab agent and tablet
    /// trusts this one CA, so replacing it means setting each of them up again (CA-ROLLOVER.md): months of work to plan.
    /// </summary>
    public static readonly TimeSpan CaPlanningPeriod = TimeSpan.FromDays(365);

    /// <summary>How long before the hospital CA expires its warning turns critical, which is told to IT on-call.</summary>
    public static readonly TimeSpan CaUrgentPeriod = TimeSpan.FromDays(90);

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

        // Whether a critical warning reaches IT at all (audit P2): who it goes to and how, the heartbeat that tells someone
        // outside when this server stops, and a drill's test warning while one is open.
        if (await AlertReadiness.WarningAsync(db, settings, options.Value, ct) is { } alertsNotReady)
        {
            messageWarnings.Add(alertsNotReady);
        }

        if (await AlertHeartbeat.OffWarningAsync(settings, options.Value, ct) is { } heartbeatOff)
        {
            messageWarnings.Add(heartbeatOff);
        }

        if (await AlertDrills.WarningAsync(db, time, ct) is { } drill)
        {
            messageWarnings.Add(drill);
        }

        var keyWarnings = new List<HealthWarning>(await SecurityControlWarningsAsync(ct));
        if (await AuditChainChecks.WarningAsync(db, ct) is { } brokenChain)
        {
            keyWarnings.Add(brokenChain);
        }

        if (await SimulatorRecordSweep.WarningAsync(db, options.Value, ct) is { } simulatorRecords)
        {
            keyWarnings.Add(simulatorRecords);
        }

        // While a next CA is prepared, the workstations and lab agents that do not hold it yet, by name.
        if (await CaRolloverDevices.WarningAsync(db, certificates, now, ct) is { } devicesNotReady)
        {
            keyWarnings.Add(devicesNotReady);
        }

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

    /// <summary>
    /// The two controls a hospital may switch off for a development machine, said loudly when they are off anywhere else
    /// (R-8): two-step sign-in for administrators, and administration only from an enrolled HC360 Desktop.
    /// </summary>
    private async Task<IReadOnlyList<HealthWarning>> SecurityControlWarningsAsync(CancellationToken ct) =>
        environment.IsDevelopment()
            ? []
            : SecurityControlWarnings(options.Value.Security.EnforceMfa, await administrationDesktops.RequiredAsync(ct));

    /// <summary>The warnings for a server that is not a development machine, from the two controls' states.</summary>
    public static IReadOnlyList<HealthWarning> SecurityControlWarnings(bool enforceMfa, bool administrationRequiresDesktop)
    {
        var warnings = new List<HealthWarning>();
        if (!enforceMfa)
        {
            warnings.Add(new("security.mfa_off", "critical",
                "Two-step sign-in is switched off for administrators (Security:EnforceMfa in the server's settings file). Switch it back on."));
        }

        if (!administrationRequiresDesktop)
        {
            warnings.Add(new("security.desktop_admin_off", "critical",
                "Administration works from any browser: the rule that it works only in HC360 Desktop is switched off. Switch it back on under Administration › Settings."));
        }

        return warnings;
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

    /// <summary>Whether a server runs the background jobs: a <c>worker</c>, or a whole Server Node (<c>all</c>).</summary>
    internal static bool RunsJobs(ServerNodeHealth server) => !string.Equals(server.Server.Role, "api", StringComparison.OrdinalIgnoreCase);

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
            RuntimeInformation.FrameworkDescription,
            clock.Last);

        var network = new NetworkInfo(
            wifi.Supported, wifi.Current, wifi.ConnectedSince, wifi.LastDisconnectAt, wifi.RecentEvents,
            LanCertificateManager.CurrentAddresses().Select(a => a.ToString()).ToList(),
            options.Value.HttpsPort, options.Value.PortalHttpsPort, options.Value.Network.EnableMdns);

        var encryption = OperatingSystem.IsWindows() ? DiskEncryption.Load(DiskEncryption.PathIn(directories)) : null;
        var storage = ReadStorage() with { Encryption = encryption };
        var certificate = new CertificateInfo(certificates.CaFingerprint, certificates.ServerExpiresAt, certificates.SubjectAlternativeNames,
            certificates.CaExpiresAt, certificates.PreviousCaFingerprint, certificates.PreviousCaExpiresAt, certificates.NextCaFingerprint);
        var power = ReadPower();

        // The machine: its data drive, and BitLocker, the settings file's secrets and the database watchdog, which exist only
        // on the Windows Server Node (SRS SEC-05, SEC-07, DED-01).
        var machine = new List<HealthWarning>();
        if (storage.FreeBytes is { } free && storage.TotalBytes is { } total && total > 0 && free < total / 10)
        {
            machine.Add(new("disk.low", "critical", $"Less than 10% disk space free on the data drive ({free / (1024 * 1024 * 1024)} GB)."));
        }

        machine.AddRange(ServerClock.Warnings(clock.Last, options.Value.TimeSync.MaxOffset));

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
        link.AddRange(WifiWarnings(network.WifiSupported, network.Wifi, wifi.AdapterMissing, options.Value.Network.WifiWeakSignalThreshold));

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
    /// The server certificate is issued again by itself 30 days before it expires, so a warning about it that lasts means
    /// that failed; one an administrator supplied is theirs to replace. The hospital CA is never made again by itself: every
    /// workstation, tablet and HC360 Desktop trusts it, so replacing it is planned a year ahead, and once it has expired
    /// nothing trusts the server. A server certificate that expires with the CA is left to the CA's warning.
    /// </summary>
    public static IReadOnlyList<HealthWarning> CertificateWarnings(CertificateInfo certificate, DateTimeOffset now)
    {
        var warnings = new List<HealthWarning>();
        var caExpires = certificate.CaExpiresAt;
        if (certificate.ServerExpiresAt is { } expires && expires - now < CertificateWarningPeriod && !(caExpires <= expires))
        {
            var urgent = expires - now < CertificateUrgentPeriod;
            var issuedHere = caExpires is not null;
            var (expiry, lockedOut) = expires <= now
                ? ($"expired on {expires:dd MMM yyyy}", "no workstation, tablet or HC360 Desktop connects")
                : ($"expires on {expires:dd MMM yyyy}", "then no workstation, tablet or HC360 Desktop connects");
            warnings.Add(new("cert.expiring", urgent ? "critical" : "warning", (urgent, issuedHere) switch
            {
                (false, _) => $"The server certificate {expiry}.",
                (true, true) => $"The server certificate {expiry} and was not issued again: {lockedOut}. Restart HC360, and if this stays, send its log to HyFib support.",
                (true, false) => $"The server certificate {expiry}: {lockedOut}. Replace the certificate file the server is set up with (Network:CertificatePath).",
            }));
        }

        if (caExpires is { } caExpired && caExpired <= now)
        {
            warnings.Add(new("cert.ca_expired", "critical", certificate.NextCaFingerprint is null
                ? $"The hospital CA expired on {caExpired:dd MMM yyyy}: workstations, tablets and HC360 Desktop no longer trust this server, and HC360 does not start again until it has a new CA. Make one now with HyFib support, as the CA rollover procedure says (CA-ROLLOVER.md)."
                : $"The hospital CA expired on {caExpired:dd MMM yyyy}: workstations, tablets and HC360 Desktop no longer trust this server, and HC360 does not start again until it switches CA. The next CA is prepared: switch to it now with 'certificates switch-ca', and every device that holds it trusts the server again (CA-ROLLOVER.md)."));
        }
        else if (caExpires is { } caExpiring && caExpiring - now < CaPlanningPeriod)
        {
            // Before the CA expires the server switches to the next one; with none prepared yet, that is the first step.
            var step = certificate.NextCaFingerprint is null
                ? "Prepare the next CA with 'certificates prepare-ca': devices take it while they still trust this one, and the server switches to it once every device holds it"
                : "The next CA is prepared: switch to it with 'certificates switch-ca' once every device holds it (the warning about the devices names those that do not)";
            warnings.Add(caExpiring - now < CaUrgentPeriod
                ? new("cert.ca_expiring", "critical",
                    $"The hospital CA expires on {caExpiring:dd MMM yyyy}: then no workstation, tablet or HC360 Desktop trusts this server. {step}, now, as the CA rollover procedure says (CA-ROLLOVER.md).")
                : new("cert.ca_expiring", "warning",
                    $"The hospital CA expires on {caExpiring:dd MMM yyyy}. {step}, in the coming months, as the CA rollover procedure says (CA-ROLLOVER.md): no device is cut off, and nothing stops."));
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

    /// <summary>
    /// The warnings about the Server Node's WiFi link (DED-06): details Windows withholds, a weak signal, an insecure or
    /// shared-password network, a link that is down, and an adapter that disappeared while the link was in use
    /// (<paramref name="adapterMissing"/>, from <see cref="WifiLinkState.AdapterMissing"/>, which says why).
    /// </summary>
    internal static IEnumerable<HealthWarning> WifiWarnings(bool supported, WifiConnectionInfo? wifi, string? adapterMissing, int weakThreshold)
    {
        if (wifi is { IsConnected: true, DetailsAvailable: false } hidden)
        {
            yield return new("wifi.details_hidden", "info", hidden.Note ?? WifiConnectionInfo.LocationPermissionNote);
        }
        else if (wifi is { IsConnected: true } connected)
        {
            if (connected.SignalQuality < weakThreshold)
            {
                yield return new("wifi.weak", "warning", $"WiFi signal is weak ({connected.SignalQuality}%).");
            }

            if (connected.IsInsecure)
            {
                yield return new("wifi.insecure", "critical", "The Server Node is on an insecure WiFi network.");
            }
            else if (connected.UsesSharedPassword)
            {
                yield return new("wifi.shared_password", "warning", "The Server Node is on a WiFi network with a shared password (WPA-Personal). Use WPA2/WPA3-Enterprise.");
            }
        }
        else if (supported && wifi is { IsConnected: false })
        {
            yield return new("wifi.down", "critical", "The Server Node's WiFi is disconnected.");
        }
        else if (!supported && adapterMissing is not null)
        {
            yield return new("wifi.adapter_missing", "critical", adapterMissing);
        }
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
                    facility?.Name, facility?.Code, certificates.CaFingerprint, time.GetUtcNow(), 1, switches, certificates.PreviousCaFingerprint,
                    certificates.NextCaFingerprint));
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public);

        HospitalCaEndpoints.Map(system);

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
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<ReauthenticateRequest>();
        admin.MapPost("/keys/signing/rotate", async (KeyRotationService keys, CancellationToken ct) => (await keys.RotateSigningKeyAsync(ct)).ToOk())
            .RequirePermission(PlatformPermissions.SystemManage);

        // Records the integrations' simulators made (audit P6): the latest sweep, a sweep now, and the IT officer's sign-off.
        admin.MapGet("/simulator-records", async (SimulatorRecordSweep sweep, CancellationToken ct) => TypedResults.Ok(await sweep.ViewAsync(ct)));
        admin.MapPost("/simulator-records/sweep", async (SimulatorRecordSweep sweep, CancellationToken ct) => TypedResults.Ok(await sweep.SweepAsync(ct)))
            .RequirePermission(PlatformPermissions.SystemManage);
        admin.MapPost("/simulator-records/sign-off", async (SignOffSimulatorRecordsRequest request, SimulatorRecordSweep sweep, CancellationToken ct) =>
                (await sweep.SignOffAsync(request, ct)).ToOk())
            .RequirePermission(PlatformPermissions.SystemManage)
            .WithValidation<SignOffSimulatorRecordsRequest>();

        // Alert drills (audit P2): a test critical warning, told as any is, and when each step happened. Whoever may see
        // System health acknowledges one: they are those the warning goes to in the app.
        admin.MapGet("/alert-drills", async (AlertDrills drills, CancellationToken ct) => TypedResults.Ok(await drills.ListAsync(ct)));
        admin.MapPost("/alert-drills", async (AlertDrills drills, CancellationToken ct) => (await drills.StartAsync(ct)).ToOk())
            .RequirePermission(PlatformPermissions.SystemManage);
        admin.MapPost("/alert-drills/{id:guid}/acknowledge", async (Guid id, AlertDrills drills, CancellationToken ct) =>
            (await drills.AcknowledgeAsync(id, ct)).ToOk());

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

/// <summary>
/// Where the server hands out its hospital CA certificates (<c>/api/v1/system</c>), as the devices and the certificate probe
/// read them: public, so a test of HC360 Desktop or the lab agent serves exactly these.
/// </summary>
public static class HospitalCaEndpoints
{
    /// <summary>
    /// The hospital CA certificates the server hands out under <paramref name="system"/> (<c>/api/v1/system</c>), public and
    /// without a secret: its CA, for tablets and browsers on the hospital WiFi and for devices being set up; while a next CA
    /// is prepared, that CA and the current CA's word for it, which HC360 Desktop and the lab agent take as a second pin
    /// (<see cref="WindowsInterop.Trust.HospitalCaFollow"/>); after a switch, the CA the current one replaced, until it
    /// expires, by which the certificate probe that pins it finds the switch.
    /// </summary>
    public static void Map(IEndpointRouteBuilder system)
    {
        system.MapGet("/ca-certificate", (LanCertificateManager certificates) =>
                certificates.CaPem is { } pem
                    ? TypedResults.File(System.Text.Encoding.ASCII.GetBytes(pem), "application/x-x509-ca-cert", "hc360-hospital-ca.crt")
                    : Results.NotFound())
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public);

        // The next CA first, then the certificate the current CA signed for its key, each PEM on lines of its own: a device
        // checks that word against the CA it pins before it holds the next one.
        system.MapGet("/ca-certificate/next", (LanCertificateManager certificates) =>
                certificates.NextCaPem is { } pem && certificates.NextCaCrossPem is { } cross
                    ? TypedResults.File(System.Text.Encoding.ASCII.GetBytes($"{pem.TrimEnd()}\n{cross.TrimEnd()}\n"), "application/x-pem-file", "hc360-hospital-ca-next.pem")
                    : Results.NotFound())
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public);

        system.MapGet("/ca-certificate/previous", (LanCertificateManager certificates) =>
                certificates.PreviousCaPem is { } pem
                    ? TypedResults.File(System.Text.Encoding.ASCII.GetBytes(pem), "application/x-x509-ca-cert", "hc360-hospital-ca-previous.crt")
                    : Results.NotFound())
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public);
    }
}
