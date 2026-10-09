using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
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
    IReadOnlyList<WifiEvent> RecentEvents, IReadOnlyList<string> IpAddresses, int HttpsPort, bool MdnsEnabled);

public sealed record DatabaseInfo(bool Reachable, string? Version, long? SizeBytes, int? Connections, string? LastMigration, string? Error);

/// <summary>
/// The data drive, and on Windows what the last BitLocker check recorded (<see cref="DiskEncryption"/>): null when it has
/// never run, and always null on other systems.
/// </summary>
public sealed record StorageInfo(string DataDirectory, long? FreeBytes, long? TotalBytes, DiskEncryptionReport? Encryption = null);

public sealed record BackupInfo(bool Enabled, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, string? LastError, long? LastSizeBytes);

public sealed record OutboxInfo(int Pending, int FailedLast24Hours, DateTimeOffset? OldestPendingAt);

public sealed record CertificateInfo(string? CaFingerprint, DateTimeOffset? ServerExpiresAt, IReadOnlyList<string> SubjectAlternativeNames);

public sealed record PowerInfo(bool Available, bool? OnBattery, int? ChargePercent, string? Source);

public sealed record HealthWarning(string Code, string Severity, string Message);

public sealed record SystemHealthReport(
    ServerInfo Server,
    NetworkInfo Network,
    DatabaseInfo Database,
    StorageInfo Storage,
    BackupInfo Backup,
    OutboxInfo Outbox,
    CertificateInfo Certificate,
    PowerInfo Power,
    IReadOnlyList<HealthWarning> Warnings,
    DateTimeOffset GeneratedAt);

public sealed record SystemInfoDto(
    string Product, string Version, string ServerName, string? FacilityName, string? FacilityCode, string? CaFingerprint, DateTimeOffset ServerTime, int ApiVersion,
    SystemFeatures Features);

/// <summary>The hospital-wide feature switches (Platform/Features), for the portal and the sign-in page before anyone signs in.</summary>
public sealed record SystemFeatures(bool PatientPortal, bool Telehealth);

public sealed record WifiSampleDto(DateTimeOffset At, bool Connected, int SignalQuality, int? RssiDbm, double? ReceiveRateMbps, string? Ssid);

/// <summary>Builds the Server Node health report shown on the administrator dashboard (DED-17).</summary>
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
    TimeProvider time)
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    /// <summary>How long a key is used before System health suggests rotating it (the key policy, SECURITY.md).</summary>
    public static readonly TimeSpan SigningKeyPeriod = TimeSpan.FromDays(365);

    public static readonly TimeSpan DataKeyPeriod = TimeSpan.FromDays(2 * 365);

    public async Task<SystemHealthReport> BuildAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
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
            options.Value.HttpsPort, options.Value.Network.EnableMdns);

        var database = await ReadDatabaseAsync(ct);
        var encryption = OperatingSystem.IsWindows() ? DiskEncryption.Load(DiskEncryption.PathIn(directories)) : null;
        var storage = ReadStorage() with { Encryption = encryption };
        var backup = await ReadBackupAsync(ct);

        var dayAgo = now.AddDays(-1);
        var outbox = new OutboxInfo(
            await db.Set<OutboxMessage>().CountAsync(m => m.Status == OutboxStatus.Pending, ct),
            // When it failed, not when it was queued: a message is retried for up to its lifetime (days) before
            // it fails.
            await db.Set<OutboxMessage>()
                .CountAsync(m => m.Status == OutboxStatus.Failed && (m.ProcessedAt ?? m.CreatedAt) > dayAgo, ct),
            await db.Set<OutboxMessage>().Where(m => m.Status == OutboxStatus.Pending).OrderBy(m => m.CreatedAt).Select(m => (DateTimeOffset?)m.CreatedAt).FirstOrDefaultAsync(ct));

        var certificate = new CertificateInfo(certificates.CaFingerprint, certificates.ServerExpiresAt, certificates.SubjectAlternativeNames);
        var power = ReadPower();

        var warnings = new List<HealthWarning>();
        if (!database.Reachable)
        {
            warnings.Add(new("db.unreachable", "critical", "The database is not reachable."));
        }

        if (storage.FreeBytes is { } free && storage.TotalBytes is { } total && total > 0 && free < total / 10)
        {
            warnings.Add(new("disk.low", "critical", $"Less than 10% disk space free on the data drive ({free / (1024 * 1024 * 1024)} GB)."));
        }

        // BitLocker and the settings file's secrets exist only on the Windows Server Node (SRS SEC-05, SEC-07).
        if (OperatingSystem.IsWindows())
        {
            warnings.AddRange(DiskEncryption.Warnings(encryption, now));
            if (PlainSecrets() is { Count: > 0 } plain)
            {
                warnings.Add(new("settings.unprotected", "warning",
                    $"The settings file holds {(plain.Count == 1 ? "a secret" : $"{plain.Count} secrets")} in plain text ({string.Join(", ", plain)}). " +
                    "Run Install-HC360Server.ps1 again, or \"HyFib.HealthCare360.Server.exe settings protect\" from an elevated prompt, to protect them with DPAPI."));
            }
        }

        if (backup.Enabled && (backup.LastSuccessAt is null || now - backup.LastSuccessAt > TimeSpan.FromHours(36)))
        {
            warnings.Add(new("backup.stale", "critical", "No successful backup in the last 36 hours."));
        }

        if (certificate.ServerExpiresAt is { } expires && expires - now < TimeSpan.FromDays(30))
        {
            warnings.Add(new("cert.expiring", "warning", $"The server certificate expires on {expires:dd MMM yyyy}."));
        }

        if (network.Wifi is { IsConnected: true, DetailsAvailable: false } hidden)
        {
            warnings.Add(new("wifi.details_hidden", "info", hidden.Note ?? WifiConnectionInfo.LocationPermissionNote));
        }
        else if (network.Wifi is { IsConnected: true } link)
        {
            if (link.SignalQuality < options.Value.Network.WifiWeakSignalThreshold)
            {
                warnings.Add(new("wifi.weak", "warning", $"WiFi signal is weak ({link.SignalQuality}%)."));
            }

            if (link.IsInsecure)
            {
                warnings.Add(new("wifi.insecure", "critical", "The Server Node is on an insecure WiFi network."));
            }
        }
        else if (network.WifiSupported && network.Wifi is { IsConnected: false })
        {
            warnings.Add(new("wifi.down", "critical", "The Server Node's WiFi is disconnected."));
        }

        if (outbox.FailedLast24Hours > 0)
        {
            warnings.Add(new("outbox.failed", "warning", $"{outbox.FailedLast24Hours} outbound message(s) failed in the last 24 hours."));
        }

        if (outbox.OldestPendingAt is { } oldest && now - oldest > TimeSpan.FromMinutes(30))
        {
            warnings.Add(new("outbox.backlog", "warning", "Outbound messages are queued for more than 30 minutes (internet down?)."));
        }

        // Reports, prescriptions and bills are printed and sent as server-made documents: say so when that is broken.
        if (pdf.Status is { Available: false } pdfStatus)
        {
            warnings.Add(new("pdf.unavailable", "warning", $"PDFs can't be made on this server: {pdfStatus.Problem}"));
        }

        if (power is { Available: true, OnBattery: true })
        {
            warnings.Add(new("power.battery", "critical", $"Running on UPS/battery power ({power.ChargePercent}% remaining)."));
        }

        if (signingKey.KeyCreatedAt is { } signedSince && now - signedSince > SigningKeyPeriod)
        {
            warnings.Add(new("keys.signing_due", "info", "The sign-in token key is over a year old. Rotate it under Encryption keys."));
        }

        if (dataKeys.Keys.Single(k => k.Current).CreatedAt is { } dataSince && now - dataSince > DataKeyPeriod)
        {
            warnings.Add(new("keys.data_due", "info", "The data encryption key is over two years old. Rotate it under Encryption keys."));
        }

        // Backups still use the original recovery key. Until HC360 made separate recovery keys, any holder of the backups
        // permission could display it; one never displayed is held by nobody.
        if (!File.Exists(RecoveryKeyStore.PathIn(directories)))
        {
            warnings.Add(await RecoveryKeyService.OriginalWasDisplayedAsync(db, ct)
                ? new("backup.recovery_key_original", "warning",
                    "Backups still use the original recovery key, which any administrator with the backups permission could display. Make a new one under Backups › Recovery key.")
                : new("backup.recovery_key_missing", "critical",
                    "Nobody holds the recovery key for the backups, so they cannot be restored on new hardware. Make one under Backups › Recovery key."));
        }

        return new SystemHealthReport(server, network, database, storage, backup, outbox, certificate, power, warnings, now);
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

        // The encryption keys (SRS SEC-07): who may see their age and what is left to move, and who may rotate them.
        admin.MapGet("/keys", async (KeyRotationService keys, CancellationToken ct) => TypedResults.Ok(await keys.StatusAsync(ct)));
        admin.MapPost("/keys/data/rotate", async (KeyRotationService keys, CancellationToken ct) => TypedResults.Ok(await keys.RotateDataKeyAsync(ct)))
            .RequirePermission(PlatformPermissions.SystemManage);
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
