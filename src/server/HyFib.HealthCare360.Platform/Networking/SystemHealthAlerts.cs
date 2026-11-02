using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// Tells people when System health turns critical, rather than waiting for someone to open it (DED-17): every five
/// minutes the server that runs the jobs builds the report, and each critical warning becomes an alert someone must
/// acknowledge (<see cref="AlertIncidents"/>, audit P2). It goes at once to everyone who may see System health, in the app,
/// and to the IT on-call number by SMS (and WhatsApp when the hospital turned it on), as Wi-Fi alerts do
/// (<see cref="WifiMonitor"/>); while nobody acknowledges it, to the second contact and then HyFib support
/// (<see cref="AlertEscalation"/>). It is told again a day after it was last told while it lasts, so one put off at
/// go-live, such as nobody holding the backups' recovery key, is not forgotten (audit P8); and again, as a new alert,
/// when it clears and comes back. The alerts are kept in the database, so a restart tells none again. The WiFi warnings are the WiFi monitor's to tell: it tells a drop, a lost adapter or an insecure network as it
/// happens, and not at all where the hospital turned it off. An alert drill's warning is told as any other, and the drill
/// keeps when, and the messages it went out in (<see cref="AlertDrills"/>, audit P2).
/// </summary>
internal sealed class SystemHealthAlerts(
    IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<SystemHealthAlerts> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>The title of the in-app notification of a critical warning.</summary>
    public const string Title = "System health: critical";

    /// <summary>How long after it was told a warning that lasts is told again.</summary>
    public static readonly TimeSpan RemindAfter = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _checking = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "System health could not be checked for critical warnings");
            }
        }
    }

    /// <summary>One check: takes each critical warning's alert the step it is due, and returns the warnings told, first or again.</summary>
    public async Task<IReadOnlyList<HealthWarning>> CheckAsync(CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var critical = (await services.GetRequiredService<SystemHealthService>().BuildAsync(cancellationToken)).Warnings
                .Where(w => w.Severity == "critical" && !w.Code.StartsWith("wifi.", StringComparison.Ordinal))
                .ToList();
            var told = await services.GetRequiredService<AlertIncidents>().CheckAsync(critical, time.GetUtcNow(), cancellationToken);
            if (told.Count > 0)
            {
                logger.LogWarning("System health is critical: {Warnings}", string.Join("; ", told.Select(w => w.Code)));
            }

            return told;
        }
        finally
        {
            _checking.Release();
        }
    }

    public override void Dispose()
    {
        _checking.Dispose();
        base.Dispose();
    }
}
