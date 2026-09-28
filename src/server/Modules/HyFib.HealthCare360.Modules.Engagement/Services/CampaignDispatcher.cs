using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Sends due campaigns in batches through <see cref="INotificationService"/> (so delivery survives internet outages via
/// the outbox). Runs every minute on job nodes only, inside the TRAI promotional window, under a cluster-wide
/// advisory lock; each batch commits with its recipients, so a crash never double-sends or loses a batch.
/// </summary>
internal sealed class CampaignDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<PlatformOptions> options,
    ILogger<CampaignDispatcher> logger) : BackgroundService
{
    private const long LockKey = 4_247_501;
    private const int BatchSize = 200;
    private const int MaxBatchesPerTick = 5;
    private const int MaxCampaignsPerTick = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DispatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Campaign dispatch failed; it will be retried");
            }
        }
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!CampaignSendWindow.IsOpen(EngagementClock.ToLocal(now)))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<HospitalDbContext>();
        var due = await db.Set<Campaign>()
            .Where(c => (c.Status == CampaignStatus.Scheduled && c.ScheduledAt <= now) || c.Status == CampaignStatus.Sending)
            .OrderBy(c => c.ScheduledAt)
            .Select(c => c.Id)
            .Take(MaxCampaignsPerTick)
            .ToListAsync(ct);

        foreach (var campaignId in due)
        {
            for (var batch = 0; batch < MaxBatchesPerTick; batch++)
            {
                if (!await SendBatchAsync(campaignId, ct))
                {
                    break;
                }
            }
        }
    }

    /// <summary>One batch in its own scope and transaction; returns false when the campaign needs no more work now.</summary>
    private async Task<bool> SendBatchAsync(Guid campaignId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<HospitalDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockKey}) AS \"Value\"").SingleAsync(ct);
        if (!locked)
        {
            return false;
        }

        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null || campaign.Status is not (CampaignStatus.Scheduled or CampaignStatus.Sending))
        {
            return false;
        }

        var baseUrl = await services.GetRequiredService<PublicLinks>().BaseUrlAsync(campaign.FacilityId, ct);
        if (baseUrl is null)
        {
            logger.LogWarning("Campaign {CampaignId} is waiting: the portal base URL setting is missing", campaign.Id);
            return false;
        }

        var now = time.GetUtcNow();
        if (campaign.Status == CampaignStatus.Scheduled)
        {
            campaign.BeginSending(now);
        }

        var campaigns = services.GetRequiredService<CampaignService>();
        var sent = await campaigns.SendBatchAsync(campaign, services.GetRequiredService<INotificationService>(), baseUrl, BatchSize, ct);
        if (sent == 0)
        {
            campaign.Complete(now);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (sent > 0)
        {
            logger.LogInformation("Campaign {CampaignId}: queued {Count} message(s)", campaign.Id, sent);
        }

        return sent > 0;
    }
}
