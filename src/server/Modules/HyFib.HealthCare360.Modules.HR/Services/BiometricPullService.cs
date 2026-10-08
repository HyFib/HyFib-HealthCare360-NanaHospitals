using System.Globalization;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>What reading a terminal brought in.</summary>
/// <param name="Read">Punches the terminal holds.</param>
/// <param name="Earlier">Punches from before what HC360 already read (or more than 90 days old), left out.</param>
/// <param name="ClockOffsetSeconds">How far the terminal's clock is ahead (positive) or behind.</param>
public sealed record TerminalPullResult(bool Success, string? Error, int Read, int Earlier, PunchIngestResult? Punches, int? ClockOffsetSeconds);

/// <summary>
/// Reads attendance terminals that cannot push (INT-BIO "pull"): on their schedule (<see cref="BiometricPullJob"/>)
/// or when HR asks. A terminal keeps every punch, so each read returns them all; punches from more than two days
/// before the latest one already read are left out, and the rest go through the same intake as pushed punches, which
/// leaves out those already received. One read of a terminal runs at a time, across nodes. Bookkeeping is written
/// with set-based updates, as for pushing terminals, so reads every few minutes do not fill the audit trail.
/// </summary>
public sealed class BiometricPullService(
    HospitalDbContext db,
    IAttendanceTerminalClient terminals,
    BiometricIngestionService ingestion,
    INotificationService notifications,
    IIntegrationLogger integrationLog,
    TimeProvider time)
{
    /// <summary>How far before the latest punch already read a new read looks (terminal clocks get corrected).</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(2);

    /// <summary>A terminal not heard from for this long (or two read intervals, if longer) has gone quiet.</summary>
    public static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(30);

    /// <summary>A terminal clock this far out is pointed out to HR.</summary>
    public const int ClockWarningSeconds = 120;

    private const string Busy = "The terminal is being read already. Try again in a minute.";

    public async Task<TerminalPullResult> PullAsync(Guid deviceId, CancellationToken ct)
    {
        await using var transaction = await JobLock.TryAcquireAsync(db, JobLock.BiometricPull, deviceId, ct);
        if (transaction is null)
        {
            return Failed(Busy);
        }

        var device = await Devices().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is not { Connection: BiometricConnection.Pull })
        {
            return Failed("This terminal sends its punches; HC360 does not read it.");
        }

        var result = await PullLockedAsync(device, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Sets the terminal's clock to the hospital's time, under the terminal's lock: terminals often take one
    /// connection at a time.
    /// </summary>
    public async Task<string?> SetClockAsync(BiometricDevice device, CancellationToken ct)
    {
        if (device.Connection != BiometricConnection.Pull || !BiometricDevice.IsAllowedPullAddress(device.PullAddress))
        {
            return "Only a terminal HC360 reads, at an IP address, can have its clock set from here.";
        }

        await using var transaction = await JobLock.TryAcquireAsync(db, JobLock.BiometricPull, device.Id, ct);
        if (transaction is null)
        {
            return Busy;
        }

        var error = await terminals.SetClockAsync(Terminal(device), time.LocalNow(), ct);
        if (error is null)
        {
            await Devices().Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ClockOffsetSeconds, 0), ct);
        }

        await transaction.CommitAsync(ct);
        return error;
    }

    /// <summary>Training: punches <paramref name="pin"/> on the simulated terminal now.</summary>
    public Task<string?> TrainingPunchAsync(BiometricDevice device, string pin, CancellationToken ct) =>
        device.Connection != BiometricConnection.Pull || !BiometricDevice.IsAllowedPullAddress(device.PullAddress)
            ? Task.FromResult<string?>("Set the terminal to be read by HC360 first.")
            : terminals.PunchOnTrainingTerminalAsync(Terminal(device), pin, time.LocalNow(), ct);

    /// <summary>
    /// Tells HR once when an active terminal that was working has gone quiet: a pushing terminal that stopped calling,
    /// or a terminal HC360 could not read for a while. Returns how many were reported.
    /// </summary>
    public async Task<int> ReportQuietTerminalsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var heardBefore = now - QuietAfter;
        var candidates = await Devices().AsNoTracking()
            .Where(d => d.IsActive && d.OfflineSince == null && d.LastHandshakeAt != null && d.LastHandshakeAt < heardBefore)
            .ToListAsync(ct);
        var reported = 0;
        foreach (var device in candidates.Where(d => now - d.LastHandshakeAt!.Value >= QuietWindow(d)))
        {
            var marked = await Devices().Where(d => d.Id == device.Id && d.OfflineSince == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.OfflineSince, now), ct);
            if (marked == 0)
            {
                continue;
            }

            var since = HrClock.ToLocal(device.LastHandshakeAt!.Value).ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);
            var why = device.Connection == BiometricConnection.Pull && device.LastPullError is { } error
                ? $"HC360 could not read it: {error}"
                : "It has stopped calling HC360. Check that it is switched on and on the network.";
            await integrationLog.LogAsync(new IntegrationLogEntry(BiometricIngestionService.IntegrationName,
                BiometricIngestionService.ProviderName(device.Connection == BiometricConnection.Pull ? PunchSource.Pull : PunchSource.Adms),
                "terminal.quiet", IntegrationDirection.Inbound, false, Reference: device.SerialNumber,
                Error: $"Not heard from since {since}."), ct);
            await notifications.NotifyPermissionHoldersAsync(HRPermissions.AttendanceManage, device.FacilityId,
                $"Attendance terminal {device.Name} not heard from",
                $"Nothing has come from {device.Name} since {since}. {why} Punches made on it stay on the terminal until it is reached.",
                "/hr/devices", "hr", "warning", ct);
            reported++;
        }

        return reported;
    }

    /// <summary>A terminal read every hour is quiet only after two missed reads.</summary>
    public static TimeSpan QuietWindow(BiometricDevice device) =>
        device.Connection == BiometricConnection.Pull
            ? TimeSpan.FromMinutes(Math.Max(QuietAfter.TotalMinutes, (2 * device.PullEveryMinutes) + 5))
            : QuietAfter;

    private async Task<TerminalPullResult> PullLockedAsync(BiometricDevice device, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!device.IsActive)
        {
            return Failed("The terminal is switched off in HC360.");
        }

        var read = BiometricDevice.IsAllowedPullAddress(device.PullAddress)
            ? await terminals.ReadAsync(Terminal(device), ct)
            : AttendanceTerminalRead.Failed("Enter the terminal's IP address on the device network.");
        if (!read.Success)
        {
            var error = Truncate(read.Error ?? "The terminal could not be read.");
            await Devices().Where(d => d.Id == device.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastPullAt, now).SetProperty(d => d.LastPullError, error), ct);
            return Failed(error);
        }

        var from = now - BiometricIngestionService.MaxBacklog;
        if (device.PulledUpTo is { } upTo && upTo - Overlap > from)
        {
            from = upTo - Overlap;
        }

        var candidates = read.Records
            .Select(r => new PunchCandidate(r.Pin.Trim(), HrClock.ToUtc(r.LocalTime), ZkTecoAttLogParser.DirectionFor(r.StatusCode), r.StatusCode, r.VerifyMode,
                r.WorkCode is { Length: <= 16 } code && code.All(char.IsAsciiLetterOrDigit) ? code : null))
            .Where(c => c.PunchedAt >= from)
            .ToList();
        var punches = await ingestion.IngestAsync(device, candidates, PunchSource.Pull, ct);

        var latest = candidates.Where(c => c.PunchedAt <= now + BiometricIngestionService.MaxClockSkew).Select(c => (DateTimeOffset?)c.PunchedAt).Max();
        var pulledUpTo = latest is { } l && (device.PulledUpTo is null || l > device.PulledUpTo) ? l : device.PulledUpTo;
        int? offset = read.TerminalTime is { } clock ? (int)Math.Round((HrClock.ToUtc(clock) - now).TotalSeconds) : null;
        await Devices().Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.LastPullAt, now)
            .SetProperty(d => d.LastPullError, (string?)null)
            .SetProperty(d => d.LastHandshakeAt, now)
            .SetProperty(d => d.PulledUpTo, pulledUpTo)
            .SetProperty(d => d.ClockOffsetSeconds, offset)
            .SetProperty(d => d.OfflineSince, (DateTimeOffset?)null), ct);
        await ingestion.LogBackAsync(device, ct);
        return new TerminalPullResult(true, null, read.Records.Count, read.Records.Count - candidates.Count, punches, offset);
    }

    private IQueryable<BiometricDevice> Devices() => db.Set<BiometricDevice>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);

    private static AttendanceTerminal Terminal(BiometricDevice device) =>
        new(device.FacilityId, device.SerialNumber, device.PullAddress!.Trim(), device.PullPort);

    private static TerminalPullResult Failed(string error) => new(false, error, 0, 0, null, null);

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}
