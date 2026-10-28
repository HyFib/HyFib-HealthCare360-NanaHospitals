using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>
/// Checks of the audit chain (<see cref="AuditChainService.VerifyAsync"/>), asked for under Administration › Audit or
/// made by the server once a day (<see cref="AuditChainCheckJob"/>). Each is written to the audit trail itself, critical
/// when the chain is broken, where no setting or screen can change it, and System health shows a critical warning while
/// the latest one found the chain broken (<see cref="WarningAsync"/>), which <see cref="SystemHealthAlerts"/> tells.
/// </summary>
public static class AuditChainChecks
{
    public const string Action = "audit.verified";

    public static async Task<AuditVerification> VerifyAndRecordAsync(AuditChainService chain, IAuditLogger audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(audit);
        var result = await chain.VerifyAsync(cancellationToken);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Security, Action, result.Message)
        {
            Severity = result.Valid ? AuditSeverity.Info : AuditSeverity.Critical,
        }, cancellationToken);
        return result;
    }

    /// <summary>The warning while the latest check found the chain broken, or null.</summary>
    public static async Task<HealthWarning?> WarningAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var latest = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Category == AuditCategories.Security && e.Action == Action)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.OccurredAt, e.Severity, e.Summary })
            .FirstOrDefaultAsync(cancellationToken);
        return latest is { Severity: AuditSeverity.Critical }
            ? new HealthWarning("audit.chain_broken", "critical",
                $"The audit trail failed its check on {latest.OccurredAt:dd MMM yyyy HH:mm} UTC: {latest.Summary} Keep the database as it is and contact HyFib support.")
            : null;
    }
}

/// <summary>
/// Checks the whole audit chain once a day, an hour after the server starts and every 24 hours from then, on the server
/// that runs the jobs: a record changed or removed in the database is found without anyone asking for a check.
/// </summary>
internal sealed class AuditChainCheckJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<AuditChainCheckJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan FirstAfter = TimeSpan.FromHours(1);

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        try
        {
            await Task.Delay(FirstAfter, time, stoppingToken);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var result = await AuditChainChecks.VerifyAndRecordAsync(
                        scope.ServiceProvider.GetRequiredService<AuditChainService>(), scope.ServiceProvider.GetRequiredService<IAuditLogger>(), stoppingToken);
                    if (!result.Valid)
                    {
                        logger.LogCritical("The audit trail failed its daily check: {Message}", result.Message);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The audit trail could not be checked");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
