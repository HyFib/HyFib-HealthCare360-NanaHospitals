using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// While a next hospital CA is prepared (<see cref="LanCertificateManager.PrepareCa"/>), the devices that do not hold it yet
/// (docs/deployment/CA-ROLLOVER.md). HC360 Desktop and the lab agent take it as a second pin by themselves, on the current
/// CA's word, within six hours of reaching the server, and say in their heartbeat which CAs they trust the server through
/// (<see cref="Device.TrustedCaFingerprints"/>). After the switch a device without it no longer trusts the server, so
/// 'certificates switch-ca' waits until every device seen lately reports it; one that reports none runs a version from before
/// devices took a next CA, and never will.
/// </summary>
public static class CaRolloverDevices
{
    /// <summary>The devices that count: those that reached the server within this long. One silent for longer is left to Administration › Devices.</summary>
    public static readonly TimeSpan SeenWithin = TimeSpan.FromDays(30);

    /// <summary>How many devices of each group the warning names before "and n more".</summary>
    public const int Named = 10;

    /// <summary>
    /// The devices seen within <see cref="SeenWithin"/> and not revoked that do not hold the next CA: by name, those that report
    /// the CAs they trust without it, and those that report none. <paramref name="nextCaFingerprint"/> is the next CA, as the
    /// devices report it (upper-case SHA-256).
    /// </summary>
    public static async Task<CaRolloverReadiness> ReadAsync(HospitalDbContext db, string nextCaFingerprint, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - SeenWithin;
        var devices = await db.Set<Device>().AsNoTracking()
            .Where(d => !d.IsRevoked && d.LastSeenAt >= since)
            .OrderBy(d => d.Name)
            .Select(d => new { d.Name, d.TrustedCaFingerprints })
            .ToListAsync(ct);
        return new CaRolloverReadiness(
            [.. devices.Where(d => d.TrustedCaFingerprints is not null && !DeviceCaFingerprints.Of(d.TrustedCaFingerprints).Contains(nextCaFingerprint, StringComparer.Ordinal)).Select(d => d.Name)],
            [.. devices.Where(d => d.TrustedCaFingerprints is null).Select(d => d.Name)],
            devices.Count);
    }

    /// <summary>System health's warning while a next CA is prepared: null otherwise, and when every device seen holds it.</summary>
    public static async Task<HealthWarning?> WarningAsync(HospitalDbContext db, LanCertificateManager certificates, DateTimeOffset now, CancellationToken ct)
    {
        if (certificates.NextCaFingerprint is not { } next || certificates.CaExpiresAt is not { } currentEnds)
        {
            return null;
        }

        return Warning(await ReadAsync(db, next, now, ct), currentEnds, now);
    }

    /// <summary>
    /// The warning for <paramref name="readiness"/>, when the current CA expires on <paramref name="currentEnds"/>: the switch
    /// must come before then. Critical in its last 90 days, as the CA's own warning is (cert.ca_expiring).
    /// </summary>
    public static HealthWarning? Warning(CaRolloverReadiness readiness, DateTimeOffset currentEnds, DateTimeOffset now)
    {
        if (readiness.NotReady == 0)
        {
            return null;
        }

        var groups = new List<string>();
        if (readiness.WithoutNextCa.Count > 0)
        {
            groups.Add($"Without it: {Names(readiness.WithoutNextCa)}.");
        }

        if (readiness.NotReporting.Count > 0)
        {
            groups.Add($"Not saying which CAs they hold (an HC360 Desktop or lab agent older than this server): {Names(readiness.NotReporting)}.");
        }

        return new HealthWarning(
            "cert.next_ca_devices",
            currentEnds - now < SystemHealthService.CaUrgentPeriod ? "critical" : "warning",
            $"The next hospital CA is prepared: {readiness.NotReady} of the {readiness.Seen} devices seen in the last 30 days do not hold it yet, so 'certificates switch-ca' waits for them, and the current CA expires on {currentEnds:dd MMM yyyy}. "
            + string.Join(" ", groups)
            + " HC360 Desktop and the lab agent take it by themselves within six hours of reaching the server: update those not saying, and look into those without it (CA-ROLLOVER.md).");
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= Named ? string.Join(", ", names) : $"{string.Join(", ", names.Take(Named))} and {names.Count - Named} more";
}

/// <summary>
/// The devices that do not hold the next hospital CA (<see cref="CaRolloverDevices.ReadAsync"/>), by name, and how many were
/// seen in all.
/// </summary>
public sealed record CaRolloverReadiness(IReadOnlyList<string> WithoutNextCa, IReadOnlyList<string> NotReporting, int Seen)
{
    /// <summary>How many devices seen do not hold the next CA, or do not say.</summary>
    public int NotReady => WithoutNextCa.Count + NotReporting.Count;
}
