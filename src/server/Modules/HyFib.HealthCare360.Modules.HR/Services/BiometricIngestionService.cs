using System.Net;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>A punch received from a terminal, already converted to a UTC instant.</summary>
public sealed record PunchCandidate(string Pin, DateTimeOffset PunchedAt, PunchDirection Direction, int? StatusCode, int? VerifyMode, string? WorkCode);

public sealed record PunchIngestResult(int Accepted, int Duplicates, int UnknownPins, int Rejected);

/// <summary>
/// Biometric attendance intake (INT-BIO). Device requests are anonymous, so everything is scoped explicitly to the
/// device's facility; punches are de-duplicated (terminals resend after network drops), implausible timestamps
/// (clock drift into the future, very old backlogs) are refused, and device bookkeeping is written with
/// set-based updates so a terminal polling every 30 seconds does not flood the audit trail.
/// </summary>
public sealed class BiometricIngestionService(HospitalDbContext db, TimeProvider time, IIntegrationLogger integrationLog)
{
    public const string IntegrationName = "biometric";
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxBacklog = TimeSpan.FromDays(90);
    private static readonly TimeSpan SeenThrottle = TimeSpan.FromMinutes(5);

    /// <summary>The registered, active device with this serial, or null (also when the request comes from a non-allowed IP).</summary>
    public async Task<BiometricDevice?> FindDeviceAsync(string? serialNumber, IPAddress? remoteIp, CancellationToken ct)
    {
        if (!AdmsProtocol.IsValidSerial(serialNumber))
        {
            return null;
        }

        var serial = serialNumber!.Trim();
        var device = await db.Set<BiometricDevice>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstOrDefaultAsync(d => d.SerialNumber == serial, ct);

        var refused = device is null || !device.IsActive
                      || (device.AllowedIpAddress is { } allowed && !string.Equals(allowed, NormalizeIp(remoteIp), StringComparison.OrdinalIgnoreCase));
        if (refused)
        {
            await integrationLog.LogAsync(new IntegrationLogEntry(IntegrationName, "zkteco-adms", "device.refused", IntegrationDirection.Inbound, false,
                Reference: serial, Error: device is null ? "Unregistered serial number." : "Device inactive or request from a non-allowed address."), ct);
            return null;
        }

        return device;
    }

    /// <summary>Constant-time check of the JSON punch API key against the stored SHA-256 hash.</summary>
    public static bool VerifyApiKey(BiometricDevice device, string? presentedKey)
    {
        if (string.IsNullOrEmpty(device.ApiKeyHash) || string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        var expected = Convert.FromHexString(device.ApiKeyHash);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string HashApiKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>A new random 256-bit device key (URL-safe).</summary>
    public static string NewApiKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? NormalizeIp(IPAddress? address) =>
        address is null ? null : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    public async Task<PunchIngestResult> IngestAsync(BiometricDevice device, IReadOnlyList<PunchCandidate> candidates, PunchSource source, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var valid = candidates
            .Where(c => ZkTecoAttLogParser.IsValidPin(c.Pin) && c.PunchedAt <= now + MaxClockSkew && c.PunchedAt >= now - MaxBacklog)
            .ToList();
        var rejected = candidates.Count - valid.Count;
        if (valid.Count == 0)
        {
            return new PunchIngestResult(0, 0, 0, rejected);
        }

        var earliest = valid.Min(c => c.PunchedAt);
        var latest = valid.Max(c => c.PunchedAt);
        var seen = (await db.Set<AttendancePunch>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(p => p.BiometricDeviceId == device.Id && p.PunchedAt >= earliest && p.PunchedAt <= latest)
                .Select(p => new { p.Pin, p.PunchedAt })
                .ToListAsync(ct))
            .Select(p => (p.Pin, p.PunchedAt.UtcTicks))
            .ToHashSet();

        var pins = valid.Select(c => c.Pin).Distinct().ToList();
        var employees = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.FacilityId == device.FacilityId && e.BiometricPin != null && pins.Contains(e.BiometricPin))
            .Select(e => new { e.Id, Pin = e.BiometricPin! })
            .ToDictionaryAsync(e => e.Pin, e => e.Id, StringComparer.Ordinal, ct);

        int accepted = 0, duplicates = 0, unknown = 0;
        foreach (var candidate in valid)
        {
            if (!seen.Add((candidate.Pin, candidate.PunchedAt.UtcTicks)))
            {
                duplicates++;
                continue;
            }

            Guid? employeeId = employees.TryGetValue(candidate.Pin, out var id) ? id : null;
            if (employeeId is null)
            {
                unknown++;
            }

            db.Add(new AttendancePunch
            {
                FacilityId = device.FacilityId,
                EmployeeId = employeeId,
                BiometricDeviceId = device.Id,
                Pin = candidate.Pin,
                PunchedAt = candidate.PunchedAt,
                Direction = candidate.Direction,
                StatusCode = candidate.StatusCode,
                VerifyMode = candidate.VerifyMode,
                WorkCode = candidate.WorkCode,
                Source = source,
                ReceivedAt = now,
            });
            accepted++;
        }

        await db.SaveChangesAsync(ct);
        await integrationLog.LogAsync(new IntegrationLogEntry(IntegrationName, ProviderName(source), "punches.received",
            IntegrationDirection.Inbound, true, Reference: device.SerialNumber,
            Response: $"accepted={accepted} duplicates={duplicates} unknownPins={unknown} rejected={rejected}"), ct);
        return new PunchIngestResult(accepted, duplicates, unknown, rejected);
    }

    /// <summary>Records a successful upload (and the terminal's new ATTLOG stamp).</summary>
    public async Task RecordPushAsync(BiometricDevice device, IPAddress? remoteIp, string? stamp, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var ip = NormalizeIp(remoteIp);
        var newStamp = AdmsProtocol.IsValidStamp(stamp) ? stamp : device.AttLogStamp;
        await db.Set<BiometricDevice>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => d.Id == device.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.LastPushAt, now)
                .SetProperty(d => d.LastHandshakeAt, now)
                .SetProperty(d => d.LastIpAddress, ip)
                .SetProperty(d => d.AttLogStamp, newStamp)
                .SetProperty(d => d.OfflineSince, (DateTimeOffset?)null), ct);
        await LogBackAsync(device, ct);
    }

    /// <summary>
    /// Records that the terminal is alive (handshake/command poll), at most every few minutes; at once when HR was told
    /// it had gone quiet.
    /// </summary>
    public async Task RecordSeenAsync(BiometricDevice device, IPAddress? remoteIp, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (device.OfflineSince is null && device.LastHandshakeAt is { } last && now - last < SeenThrottle)
        {
            return;
        }

        var ip = NormalizeIp(remoteIp);
        await db.Set<BiometricDevice>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => d.Id == device.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.LastHandshakeAt, now)
                .SetProperty(d => d.LastIpAddress, ip)
                .SetProperty(d => d.OfflineSince, (DateTimeOffset?)null), ct);
        await LogBackAsync(device, ct);
    }

    /// <summary>A terminal HR was told had gone quiet is heard from again: the integrations dashboard shows it working.</summary>
    internal Task LogBackAsync(BiometricDevice device, CancellationToken ct) =>
        device.OfflineSince is null
            ? Task.CompletedTask
            : integrationLog.LogAsync(new IntegrationLogEntry(IntegrationName, ProviderName(device.Connection == BiometricConnection.Pull ? PunchSource.Pull : PunchSource.Adms),
                "terminal.back", IntegrationDirection.Inbound, true, Reference: device.SerialNumber), ct);

    internal static string ProviderName(PunchSource source) => source switch
    {
        PunchSource.Adms => "zkteco-adms",
        PunchSource.Pull => "pull",
        _ => "device-api",
    };
}
