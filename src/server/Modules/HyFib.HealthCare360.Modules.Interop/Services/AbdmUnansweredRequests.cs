using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// Requests the gateway has not answered within <see cref="AbdmGatewaySettings.AnswerTimeout"/>, marked failed
/// (<see cref="AbdmLinkingService.NoAnswerCode"/>) so that they end and staff may send them again, instead of waiting
/// for a callback that was lost:
/// <list type="bullet">
/// <item>link requests awaiting their link token (<see cref="AbdmLinkStatus.AwaitingToken"/>) or the answer to their
/// care contexts (<see cref="AbdmLinkStatus.Sent"/>), from when they were sent;</item>
/// <item>consent requests the consent manager has not said it has (<see cref="AbdmConsentRequestStatus.Sent"/>, awaiting
/// its on-init), from when they were made.</item>
/// </list>
/// Only what waits on the gateway's own answer, which comes in seconds, is swept. A consent request the consent manager
/// has (<see cref="AbdmConsentRequestStatus.Requested"/>) waits on the patient, who decides in their ABHA app in their own
/// time; the consent manager tells the hospital when it expires. Marking it failed, and asking again, would put a second
/// request in the patient's app. The mark is HC360's guess, not the gateway's word: an answer that comes later is still
/// taken (<see cref="AbdmLinkingService.OnLinkTokenAsync"/>, <see cref="AbdmLinkingService.OnCareContextsAsync"/>,
/// <see cref="AbdmConsentRequestService.OnInitAsync"/>). A request answered as it is swept is left as answered.
/// </summary>
internal sealed class AbdmUnansweredRequests(HospitalDbContext db, AbdmGatewayConfiguration configuration, TimeProvider time, ILogger<AbdmUnansweredRequests> log)
{
    /// <summary>The most of each kind marked at one go; the rest at the next sweep.</summary>
    public const int Batch = 200;

    /// <summary>Marks the requests unanswered past the timeout as failed: how many link and consent requests.</summary>
    public async Task<(int Links, int Consents)> SweepAsync(CancellationToken ct)
    {
        var (settings, _, _) = await configuration.ReadAsync(ct);
        var timeout = settings.AnswerTimeout;
        var now = time.GetUtcNow();
        var cutoff = now - timeout;
        var minutes = (int)timeout.TotalMinutes;

        // Every facility's: the gateway's callbacks come with none. Each row is saved on its own, so one answered as it is
        // swept (its version changed) is left as answered and does not stop the others.
        var links = 0;
        var linkIds = await db.Set<AbdmLinkRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => (r.Status == AbdmLinkStatus.AwaitingToken || r.Status == AbdmLinkStatus.Sent) && (r.SentAt ?? r.CreatedAt) < cutoff)
            .OrderBy(r => r.SentAt)
            .Select(r => r.Id)
            .Take(Batch)
            .ToListAsync(ct);
        foreach (var id in linkIds)
        {
            var request = await db.Set<AbdmLinkRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstAsync(r => r.Id == id, ct);
            if (request.Status is not (AbdmLinkStatus.AwaitingToken or AbdmLinkStatus.Sent))
            {
                continue;
            }

            request.Fail(AbdmLinkingService.NoAnswerCode, LinkMessage(request.Status, minutes), now);
            links += await SaveAsync(ct);
        }

        var consents = 0;
        var consentIds = await db.Set<AbdmConsentRequest>().AsNoTracking()
            .Where(r => r.Status == AbdmConsentRequestStatus.Sent && r.CreatedAt < cutoff)
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.Id)
            .Take(Batch)
            .ToListAsync(ct);
        foreach (var id in consentIds)
        {
            var request = await db.Set<AbdmConsentRequest>().FirstAsync(r => r.Id == id, ct);
            if (request.Status != AbdmConsentRequestStatus.Sent)
            {
                continue;
            }

            request.Status = AbdmConsentRequestStatus.Failed;
            request.Error = ConsentMessage(minutes);
            consents += await SaveAsync(ct);
        }

        if (links + consents > 0)
        {
            log.LogWarning("ABDM did not answer {Links} link request(s) and {Consents} consent request(s) within {Minutes} minutes: marked failed", links, consents, minutes);
        }

        return (links, consents);
    }

    /// <summary>Why a link request was marked failed, as staff read it.</summary>
    internal static string LinkMessage(AbdmLinkStatus waitingFor, int minutes) => waitingFor == AbdmLinkStatus.AwaitingToken
        ? $"ABDM did not send a link token within {minutes} minutes. Link the records again; if ABDM answers later, its answer is still taken."
        : $"ABDM did not say within {minutes} minutes whether it linked the records. Link them again; if ABDM answers later, its answer is still taken.";

    /// <summary>Why a consent request was marked failed, as staff read it.</summary>
    internal static string ConsentMessage(int minutes) =>
        $"The consent manager did not say within {minutes} minutes that it has the request. Ask again; if it answers later, this request goes ahead too.";

    private async Task<int> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return 1;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Answered as it was swept: the answer stands.
            db.ChangeTracker.Clear();
            return 0;
        }
    }
}

/// <summary>
/// Sweeps the requests the gateway has not answered (<see cref="AbdmUnansweredRequests"/>) every
/// <see cref="Interval"/>, and once at start-up to end what waited through downtime. Skipped on API-only nodes.
/// </summary>
internal sealed class AbdmUnansweredRequestSweeper(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<PlatformOptions> options,
    ILogger<AbdmUnansweredRequestSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AbdmUnansweredRequests>().SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sweeping the ABDM requests the gateway did not answer failed; it runs again in a minute");
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
