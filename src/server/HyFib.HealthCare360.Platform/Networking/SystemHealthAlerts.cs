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
/// when it clears and comes back. The alerts are kept in the database, so a restart tells none again. The link warnings
/// are the link monitor's to tell (<see cref="ToldByTheLinkMonitor"/>): it tells a WiFi drop, a lost adapter or an
/// insecure network, and the network cable lost, as it happens, and not at all where the hospital turned it off. A server
/// with no link at all (<c>network.down</c>), as when it started with its cable out, is told here: no monitor tells it.
/// An alert drill's warning is told as any other, and the drill keeps when, and the messages it went out in
/// (<see cref="AlertDrills"/>, audit P2). When a server of several that had stopped reporting reports again, everyone its
/// alert reached is told it is over (<see cref="AlertIncidents"/>).
/// </summary>
internal sealed class SystemHealthAlerts(
    IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<SystemHealthAlerts> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>The title of the in-app notification of a critical warning.</summary>
    public const string Title = "System health: critical";

    /// <summary>The title of the in-app notification that a server which had stopped reporting reports again.</summary>
    public const string BackTitle = "System health: server reporting again";

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
            var report = await services.GetRequiredService<SystemHealthService>().BuildAsync(cancellationToken);
            var told = await CheckAsync(report, services.GetRequiredService<AlertIncidents>(), time.GetUtcNow(), cancellationToken);
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

    /// <summary>
    /// The alerts of <paramref name="report"/> at <paramref name="now"/>: of its warnings, the critical ones but those the
    /// link monitor tells; and with several servers, those that report now, from the same report, for a server whose alert
    /// for not reporting clears is back only if it is among them. One stopped on purpose, or removed, is not.
    /// </summary>
    internal static Task<IReadOnlyList<HealthWarning>> CheckAsync(SystemHealthReport report, AlertIncidents alerts, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(alerts);
        return alerts.CheckAsync(
            [.. report.Warnings.Where(w => w.Severity == "critical" && !ToldByTheLinkMonitor(w.Code))],
            now,
            report.Servers?.Where(s => s.Reporting).Select(s => s.Server.MachineName).ToList(),
            ct);
    }

    /// <summary>A warning the link monitor tells as it happens (<see cref="WifiMonitor"/>), so that one loss is not told twice.</summary>
    internal static bool ToldByTheLinkMonitor(string code) =>
        code.StartsWith("wifi.", StringComparison.Ordinal) || string.Equals(code, "network.cable_down", StringComparison.Ordinal);

    public override void Dispose()
    {
        _checking.Dispose();
        base.Dispose();
    }
}
