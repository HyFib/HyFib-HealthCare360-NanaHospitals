using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// HC360 Desktop and lab agents older than the minimum the hospital supports (<see cref="ClientVersionOptions"/>). Each
/// device records its version in its heartbeat (<see cref="Device.AppVersion"/>); after a server update, the devices below
/// the minimum are named in System health, and the answer to their heartbeat carries <see cref="UpdateRequiredHeader"/> so the
/// program can tell its user. Nothing is refused: a warning and a header only, so a ward is never locked out mid-shift.
/// </summary>
public static class OutdatedClients
{
    /// <summary>
    /// Set on the heartbeat answer of a device below its minimum; the value is the version to update to. The desktop and the
    /// lab agent may show it. Absent when the device is up to date.
    /// </summary>
    public const string UpdateRequiredHeader = "X-HC360-Update-Required";

    public const string WarningCode = "clients.outdated";

    /// <summary>The devices that count: those that reached the server within this long. One silent for longer is left to Administration › Devices.</summary>
    public static readonly TimeSpan SeenWithin = TimeSpan.FromDays(30);

    /// <summary>How many devices the warning names before "and n more".</summary>
    public const int Named = 10;

    /// <summary>
    /// The oldest version a device of <paramref name="kind"/> may run: the setting, or when it is unset (or not a version) the
    /// server's release as major.minor.0.
    /// </summary>
    public static Version MinimumFor(DeviceKind kind, ClientVersionOptions options, string serverVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = Parse(kind == DeviceKind.LabAgent ? options.MinimumLabAgentVersion : options.MinimumDesktopVersion);
        if (configured is not null)
        {
            return Normalise(configured);
        }

        var server = Parse(serverVersion) ?? new Version(1, 0, 0);
        return new Version(server.Major, Math.Max(server.Minor, 0), 0);
    }

    /// <summary>
    /// A reported version as numbers: a label after a dash and build metadata after a plus are left out, so 1.0.0-rc.1 is 1.0.0.
    /// Null for none, or for text that is not a version.
    /// </summary>
    public static Version? Parse(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported))
        {
            return null;
        }

        var numbers = reported.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(numbers, out var version) ? version : null;
    }

    /// <summary>True when <paramref name="reported"/> is older than <paramref name="minimum"/>, or says nothing, or is not a version (a program from before devices reported one).</summary>
    public static bool IsBelow(string? reported, Version minimum) => Parse(reported) is not { } version || Normalise(version) < Normalise(minimum);

    /// <summary>
    /// Adds <see cref="UpdateRequiredHeader"/> to a heartbeat's answer when the device is below its minimum. <paramref name="reported"/>
    /// is the version it reports now, or the one on record when it reported none.
    /// </summary>
    public static void ApplyHeader(HttpResponse response, DeviceKind kind, string? reported, ClientVersionOptions options, string serverVersion)
    {
        ArgumentNullException.ThrowIfNull(response);
        var minimum = MinimumFor(kind, options, serverVersion);
        if (IsBelow(reported, minimum))
        {
            response.Headers[UpdateRequiredHeader] = minimum.ToString(3);
        }
    }

    /// <summary>The devices seen within <see cref="SeenWithin"/> and not revoked that run a version below their minimum.</summary>
    public static async Task<OutdatedClientList> ReadAsync(HospitalDbContext db, ClientVersionOptions options, string serverVersion, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var since = now - SeenWithin;
        var devices = await db.Set<Device>().AsNoTracking()
            .Where(d => !d.IsRevoked && d.LastSeenAt >= since)
            .OrderBy(d => d.Name)
            .Select(d => new { d.Name, d.Kind, d.AppVersion })
            .ToListAsync(ct);
        var desktops = new List<string>();
        var agents = new List<string>();
        foreach (var device in devices.Where(d => IsBelow(d.AppVersion, MinimumFor(d.Kind, options, serverVersion))))
        {
            (device.Kind == DeviceKind.LabAgent ? agents : desktops).Add(device.AppVersion is null ? $"{device.Name} (no version)" : $"{device.Name} ({device.AppVersion})");
        }

        return new OutdatedClientList(
            desktops, agents, MinimumFor(DeviceKind.Workstation, options, serverVersion).ToString(3), MinimumFor(DeviceKind.LabAgent, options, serverVersion).ToString(3));
    }

    /// <summary>System health's warning: null when every device seen is up to date.</summary>
    public static async Task<HealthWarning?> WarningAsync(HospitalDbContext db, ClientVersionOptions options, string serverVersion, DateTimeOffset now, CancellationToken ct) =>
        Warning(await ReadAsync(db, options, serverVersion, now, ct));

    public static HealthWarning? Warning(OutdatedClientList outdated)
    {
        ArgumentNullException.ThrowIfNull(outdated);
        if (outdated.Count == 0)
        {
            return null;
        }

        var groups = new List<string>();
        if (outdated.Desktops.Count > 0)
        {
            groups.Add($"HC360 Desktop below {outdated.MinimumDesktop}: {Names(outdated.Desktops)}.");
        }

        if (outdated.LabAgents.Count > 0)
        {
            groups.Add($"Lab agent below {outdated.MinimumLabAgent}: {Names(outdated.LabAgents)}.");
        }

        return new HealthWarning(
            WarningCode,
            "warning",
            string.Create(CultureInfo.InvariantCulture, $"{outdated.Count} device(s) seen in the last 30 days run an older HC360 than this server supports. ")
            + string.Join(" ", groups)
            + " They keep working and have been told to update; install the new version at a quiet time (DEPLOYMENT.md, section 7).");
    }

    // A version read as 1.0 is 1.0.0: System.Version holds an unset build as -1, which compares lower than 0.
    private static Version Normalise(Version version) => new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));

    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= Named ? string.Join(", ", names) : $"{string.Join(", ", names.Take(Named))} and {names.Count - Named} more";
}

/// <summary>The devices below their minimum version, by name and the version they report, and the minimums they were held to.</summary>
public sealed record OutdatedClientList(IReadOnlyList<string> Desktops, IReadOnlyList<string> LabAgents, string MinimumDesktop, string MinimumLabAgent)
{
    public int Count => Desktops.Count + LabAgents.Count;
}
