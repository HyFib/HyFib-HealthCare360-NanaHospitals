using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// During a hospital CA rollover, the devices that have not taken the new CA yet (docs/deployment/CA-ROLLOVER.md). HC360
/// Desktop and the lab agent take it by themselves, on the old CA's word, within six hours of reaching the server, and say
/// in their heartbeat which CAs they trust the server through (<see cref="Device.TrustedCaFingerprints"/>). A device seen
/// lately that reports without the new CA, or reports none (a version from before devices followed a rollover), stops
/// trusting the server when the previous CA expires.
/// </summary>
public static class CaRolloverDevices
{
    /// <summary>The devices that count: those that reached the server within this long. One silent for longer is left to Administration › Devices.</summary>
    public static readonly TimeSpan SeenWithin = TimeSpan.FromDays(30);

    /// <summary>How many devices of each group the warning names before "and n more".</summary>
    public const int Named = 10;

    /// <summary>
    /// The devices seen within <see cref="SeenWithin"/> and not revoked that have not taken the current CA: by name, those
    /// that report the CAs they trust without it, and those that report none. <paramref name="currentCaFingerprint"/> is the
    /// server's CA, as the devices report it (upper-case SHA-256).
    /// </summary>
    public static async Task<CaRolloverReadiness> ReadAsync(HospitalDbContext db, string currentCaFingerprint, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - SeenWithin;
        var devices = await db.Set<Device>().AsNoTracking()
            .Where(d => !d.IsRevoked && d.LastSeenAt >= since)
            .OrderBy(d => d.Name)
            .Select(d => new { d.Name, d.TrustedCaFingerprints })
            .ToListAsync(ct);
        return new CaRolloverReadiness(
            [.. devices.Where(d => d.TrustedCaFingerprints is not null && !DeviceCaFingerprints.Of(d.TrustedCaFingerprints).Contains(currentCaFingerprint, StringComparer.Ordinal)).Select(d => d.Name)],
            [.. devices.Where(d => d.TrustedCaFingerprints is null).Select(d => d.Name)],
            devices.Count);
    }

    /// <summary>
    /// System health's warning while the previous CA still vouches for the current one: null outside a rollover, and when every
    /// device seen has taken the new CA.
    /// </summary>
    public static async Task<HealthWarning?> WarningAsync(HospitalDbContext db, LanCertificateManager certificates, DateTimeOffset now, CancellationToken ct)
    {
        if (certificates.CrossSignedUntil is not { } until || until <= now || certificates.CaFingerprint is not { } current)
        {
            return null;
        }

        return Warning(await ReadAsync(db, current, now, ct), until, now);
    }

    /// <summary>
    /// The warning for <paramref name="readiness"/>, when the devices that have not moved stop trusting the server on
    /// <paramref name="until"/>: critical in the last 90 days, as the previous CA's own warning is (cert.rollover_ending).
    /// </summary>
    public static HealthWarning? Warning(CaRolloverReadiness readiness, DateTimeOffset until, DateTimeOffset now)
    {
        var behind = readiness.OnPreviousCa.Count + readiness.NotReporting.Count;
        if (behind == 0)
        {
            return null;
        }

        var groups = new List<string>();
        if (readiness.OnPreviousCa.Count > 0)
        {
            groups.Add($"On the previous CA: {Names(readiness.OnPreviousCa)}.");
        }

        if (readiness.NotReporting.Count > 0)
        {
            groups.Add($"Not saying which CA they trust (an HC360 Desktop or lab agent older than this server): {Names(readiness.NotReporting)}.");
        }

        return new HealthWarning(
            "cert.rollover_devices",
            until - now < SystemHealthService.CaUrgentPeriod ? "critical" : "warning",
            $"The hospital CA was rolled over: {behind} of the {readiness.Seen} devices seen in the last 30 days have not taken the new CA, and stop trusting this server on {until:dd MMM yyyy}. "
            + string.Join(" ", groups)
            + " HC360 Desktop and the lab agent take the new CA by themselves within six hours of reaching the server: update those not saying, and look into those still on the previous CA (CA-ROLLOVER.md).");
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= Named ? string.Join(", ", names) : $"{string.Join(", ", names.Take(Named))} and {names.Count - Named} more";
}

/// <summary>
/// The devices that have not taken the current hospital CA (<see cref="CaRolloverDevices.ReadAsync"/>), by name, and how many
/// were seen in all.
/// </summary>
public sealed record CaRolloverReadiness(IReadOnlyList<string> OnPreviousCa, IReadOnlyList<string> NotReporting, int Seen);
