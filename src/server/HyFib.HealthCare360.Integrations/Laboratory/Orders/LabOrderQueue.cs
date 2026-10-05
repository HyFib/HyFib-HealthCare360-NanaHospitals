using System.Text.RegularExpressions;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Integrations.Laboratory.Orders;

/// <summary>One order to send an analyser unasked: its queue row, why, the order as it is now and the claim.</summary>
internal sealed record LabDownload(Guid Id, InstrumentOrderAction Action, InstrumentOrder Order, Guid Claim = default);

/// <summary>
/// The per-analyser queue of orders to send unasked (INT-LAB order download).
/// <list type="bullet">
/// <item>Rows are added by the <c>lab.analyser.order</c> outbox message, in the dispatcher's transaction.</item>
/// <item>A connected analyser's session takes its due rows oldest first, claims each one, and builds the order from
/// the LIS at that moment (<see cref="IInstrumentResultSink.GetOrderToSendAsync"/>). It extends the claim while it
/// sends (a send can outlast <see cref="ClaimTime"/>), and only the claim's holder records the outcome, so a lapsed
/// claim never lets two sessions send and record the same order.</item>
/// <item>Rows wait while the analyser is not connected. Sends the analyser refuses are retried with a growing wait,
/// at most <see cref="MaxAttempts"/> times; rows not sent within <see cref="Lifetime"/> expire.</item>
/// </list>
/// </summary>
internal sealed partial class LabOrderQueue(HospitalDbContext db, TimeProvider time, ILogger<LabOrderQueue> logger)
{
    public const int MaxAttempts = 10;

    /// <summary>A sample not run within three days is recollected rather than run, so its order is no longer sent.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>
    /// How long a claim lasts unless the session extends it (every <see cref="LabRuntimeOptions.ClaimRenewInterval"/>).
    /// </summary>
    internal static readonly TimeSpan ClaimTime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan KeepFinished = TimeSpan.FromDays(30);

    /// <summary>Adds one row per analyser named by the request (not saved: the outbox dispatcher saves it with the message).</summary>
    public async Task AddAsync(OutboxMessage message, InstrumentOrderRequest request, CancellationToken cancellationToken)
    {
        var barcode = request.SampleBarcode?.Trim() ?? string.Empty;
        if (barcode.Length is 0 or > 64 || !Enum.IsDefined(request.Action))
        {
            logger.LogWarning("Analyser order request {MessageId} ignored: it names no valid sample or action", message.Id);
            return;
        }

        if (await db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(d => d.SourceMessageId == message.Id, cancellationToken))
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var target in (request.Analysers ?? []).DistinctBy(t => t.InstrumentCode?.Trim().ToUpperInvariant()))
        {
            var code = target.InstrumentCode?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!CodePattern().IsMatch(code))
            {
                logger.LogWarning("Analyser order request {MessageId}: an analyser code is not valid and was skipped", message.Id);
                continue;
            }

            db.Add(new LabOrderDownload
            {
                FacilityId = message.FacilityId ?? Guid.Empty,
                InstrumentCode = code,
                SampleBarcode = barcode,
                Action = request.Action,
                ChannelCodes = (target.ChannelCodes ?? []).Select(c => c.Trim()).Where(c => c.Length is > 0 and <= 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList(),
                Status = LabOrderDownloadStatus.Pending,
                CreatedAt = now,
                NextAttemptAt = now,
                SourceMessageId = message.Id,
            });
        }
    }

    /// <summary>
    /// The analyser's due orders (at most <paramref name="max"/>), claimed and built now. Rows with nothing to send are
    /// closed as skipped with the reason; when host queries and order download are off for the analyser, all are.
    /// </summary>
    public async Task<IReadOnlyList<LabDownload>> NextAsync(LabInstrumentSettings instrument, IInstrumentResultSink sink, int max, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var code = instrument.Code.Trim().ToUpperInvariant();
        var due = await db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(d => d.InstrumentCode == code && d.Status == LabOrderDownloadStatus.Pending && d.NextAttemptAt <= now
                        && (d.ClaimedUntil == null || d.ClaimedUntil < now))
            .OrderBy(d => d.CreatedAt)
            .Take(Math.Clamp(max, 1, 50))
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return [];
        }

        if (OffReason(await sink.GetInstrumentAsync(code, cancellationToken), instrument) is { } off)
        {
            await FinishAsync(due.Select(d => d.Id).ToList(), LabOrderDownloadStatus.Skipped, off, cancellationToken);
            return [];
        }

        var claim = Guid.CreateVersion7();
        var downloads = new List<LabDownload>();
        foreach (var row in due)
        {
            if (!await ClaimAsync(row.Id, claim, now, cancellationToken))
            {
                continue;
            }

            var current = await sink.GetOrderToSendAsync(code, row.SampleBarcode, cancellationToken);
            var channels = ToSend(row.Action, row.ChannelCodes, current?.ChannelCodes ?? []);
            var skip = row.Action != InstrumentOrderAction.Cancel && current is null ? "The sample is no longer in the lab."
                : channels.Count == 0 ? NothingToSend(row.Action)
                : row.Action == InstrumentOrderAction.Cancel && !await WasSentAsync(code, row.SampleBarcode, cancellationToken) ? "The analyser was never sent this sample."
                : null;
            if (skip is not null)
            {
                await FinishAsync([row.Id], LabOrderDownloadStatus.Skipped, skip, cancellationToken);
                continue;
            }

            var order = (current ?? new InstrumentOrder(row.SampleBarcode, string.Empty, null, null, [])) with { ChannelCodes = channels };
            downloads.Add(new LabDownload(row.Id, row.Action, order, claim));
        }

        return downloads;
    }

    /// <summary>Keeps the claim on rows being sent; returns how many rows the claim still holds.</summary>
    public Task<int> ExtendClaimAsync(IReadOnlyCollection<Guid> ids, Guid claim, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + ClaimTime;
        return db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => ids.Contains(d.Id) && d.ClaimedBy == claim && d.Status == LabOrderDownloadStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ClaimedUntil, until), cancellationToken);
    }

    /// <summary>
    /// Records a send: accepted, or tried again later (failed after <see cref="MaxAttempts"/>). Only rows still held by
    /// <paramref name="claim"/> are recorded; a row another session claimed since is that session's to record.
    /// </summary>
    public async Task CompleteAsync(
        IReadOnlyCollection<Guid> ids, Guid claim, bool delivered, string? error, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var rows = await db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => ids.Contains(d.Id) && d.Status == LabOrderDownloadStatus.Pending)
            .ToListAsync(cancellationToken);
        var lost = rows.Count(r => r.ClaimedBy != claim);
        if (lost > 0)
        {
            logger.LogWarning(
                "{Count} analyser order(s) were claimed by another session during the send; not recorded", lost);
        }

        foreach (var row in rows.Where(r => r.ClaimedBy == claim))
        {
            row.Attempts++;
            row.ClaimedUntil = null;
            row.ClaimedBy = null;
            if (delivered)
            {
                row.Status = LabOrderDownloadStatus.Sent;
                row.CompletedAt = now;
                row.LastError = null;
                continue;
            }

            row.LastError = IntegrationTrace.Truncate(error ?? "The analyser did not accept the order.", 500);
            if (row.Attempts >= MaxAttempts)
            {
                row.Status = LabOrderDownloadStatus.Failed;
                row.CompletedAt = now;
            }
            else
            {
                row.NextAttemptAt = now + RetryDelay(row.Attempts);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Expires orders the analyser was not connected for in time and deletes finished rows after 30 days.</summary>
    public async Task<(int Expired, int Deleted)> HousekeepAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var staleBefore = now - Lifetime;
        var expired = await db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => d.Status == LabOrderDownloadStatus.Pending && d.CreatedAt < staleBefore)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, LabOrderDownloadStatus.Expired)
                .SetProperty(d => d.CompletedAt, now)
                .SetProperty(d => d.ClaimedUntil, (DateTimeOffset?)null)
                .SetProperty(d => d.ClaimedBy, (Guid?)null)
                .SetProperty(d => d.LastError, "Not sent within 72 hours: the analyser was not connected or kept refusing it."), cancellationToken);
        var finishedBefore = now - KeepFinished;
        var deleted = await db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => d.Status != LabOrderDownloadStatus.Pending && d.CompletedAt < finishedBefore)
            .ExecuteDeleteAsync(cancellationToken);
        return (expired, deleted);
    }

    /// <summary>
    /// What a row sends, from what it asked for and what the sample still needs from this analyser now: everything still
    /// to run for a new sample; the tests asked for that are still to run for a rerun; and for a cancellation the tests
    /// asked for that nothing on the sample still needs.
    /// </summary>
    internal static IReadOnlyList<string> ToSend(InstrumentOrderAction action, IReadOnlyCollection<string> requested, IReadOnlyCollection<string> stillToRun) => action switch
    {
        InstrumentOrderAction.New => [.. stillToRun],
        InstrumentOrderAction.Rerun => requested.Where(c => stillToRun.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList(),
        InstrumentOrderAction.Cancel => requested.Where(c => !stillToRun.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList(),
        _ => [],
    };

    /// <summary>
    /// Why host queries and order download are off for an analyser, or null when they are on: Bidirectional in Lab setup
    /// decides, and a connection still carrying the retired <c>"hostQuery": false</c> keeps them off.
    /// </summary>
    internal static string? OffReason(InstrumentProfile? profile, LabInstrumentSettings instrument) =>
        profile is null ? $"Analyser {instrument.Code} is not set up in Lab setup."
        : !profile.IsActive ? $"Analyser {profile.Code} is marked not in use in Lab setup."
        : !profile.IsBidirectional ? $"Host queries and order download are off: {profile.Code} is not bidirectional in Lab setup."
        : instrument.HostQuery == false ? $"Host queries and order download are off: the connection of {profile.Code} still has the retired \"hostQuery\": false. Save its connection in Lab setup to clear it."
        : null;

    /// <summary>30 s, 1, 2, 4 and 8 minutes, then every 10 minutes.</summary>
    internal static TimeSpan RetryDelay(int attempts) => TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Max(0, attempts - 1)), 600));

    private static string NothingToSend(InstrumentOrderAction action) => action switch
    {
        InstrumentOrderAction.New => "Nothing on the sample is waiting for this analyser.",
        InstrumentOrderAction.Rerun => "The test is no longer waiting for a result.",
        _ => "The cancelled tests are ordered on the sample again.",
    };

    private async Task<bool> ClaimAsync(Guid id, Guid claim, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var until = now + ClaimTime;
        var claimed = await db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => d.Id == id && d.Status == LabOrderDownloadStatus.Pending && (d.ClaimedUntil == null || d.ClaimedUntil < now))
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.ClaimedUntil, until).SetProperty(d => d.ClaimedBy, claim), cancellationToken);
        return claimed == 1;
    }

    private Task<bool> WasSentAsync(string code, string barcode, CancellationToken cancellationToken) =>
        db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(d => d.InstrumentCode == code && d.SampleBarcode == barcode && d.Status == LabOrderDownloadStatus.Sent
                           && d.Action != InstrumentOrderAction.Cancel, cancellationToken);

    private async Task FinishAsync(IReadOnlyCollection<Guid> ids, LabOrderDownloadStatus status, string reason, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => ids.Contains(d.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, status)
                .SetProperty(d => d.CompletedAt, now)
                .SetProperty(d => d.ClaimedUntil, (DateTimeOffset?)null)
                .SetProperty(d => d.ClaimedBy, (Guid?)null)
                .SetProperty(d => d.LastError, reason), cancellationToken);
        logger.LogDebug("{Count} analyser order(s) closed as {Status}: {Reason}", ids.Count, status, reason);
    }

    [GeneratedRegex("^[A-Z0-9_-]{1,32}$")]
    private static partial Regex CodePattern();
}
