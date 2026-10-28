using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// Tells people when System health turns critical, rather than waiting for someone to open it (DED-17): every five
/// minutes the server that runs the jobs builds the report, and each critical warning that was not there at the last check
/// goes to everyone who may see System health, in the app, and to the IT on-call number by SMS (and WhatsApp when the
/// hospital turned it on), as Wi-Fi alerts do (<see cref="WifiMonitor"/>). A warning is told again a day after it was
/// last told while it lasts, so one put off at go-live, such as nobody holding the backups' recovery key, is not forgotten
/// (audit P8); and again as soon as it clears and comes back. After a restart the ones still there are told once more.
/// The WiFi warnings are the WiFi monitor's to tell: it tells a drop, a lost adapter or an insecure network as it happens,
/// and not at all where the hospital turned it off.
/// </summary>
internal sealed class SystemHealthAlerts(
    IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<SystemHealthAlerts> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>How long after it was told a warning that lasts is told again.</summary>
    public static readonly TimeSpan RemindAfter = TimeSpan.FromDays(1);

    // When each warning still there was last told.
    private readonly Dictionary<string, DateTimeOffset> _told = new(StringComparer.Ordinal);
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

    /// <summary>One check: tells the critical warnings not told since they appeared, or not for a day, and returns them.</summary>
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

            // One that cleared is told again if it comes back.
            foreach (var cleared in _told.Keys.Except(critical.Select(Key)).ToList())
            {
                _told.Remove(cleared);
            }

            var now = time.GetUtcNow();
            var fresh = critical.Where(w => !_told.TryGetValue(Key(w), out var told) || now - told >= RemindAfter).ToList();
            if (fresh.Count == 0)
            {
                return fresh;
            }

            var notifications = services.GetRequiredService<INotificationService>();
            var onCall = await WifiMonitor.OnCallAsync(services.GetRequiredService<ISettingsService>(), cancellationToken);
            foreach (var warning in fresh)
            {
                var server = warning.Server ?? Environment.MachineName;
                await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, "System health: critical",
                    warning.Server is null ? warning.Message : $"{warning.Message} (server {warning.Server})", "/admin/system", "system", "critical", cancellationToken);

                // Tried through an internet outage until the reminder after it is due, which takes its place while the warning
                // lasts: the outage ends with one message, not one for every day it lasted.
                if (!string.IsNullOrWhiteSpace(onCall.Phone))
                {
                    notifications.Queue("system.alert", new NotificationRecipient(Phone.Normalize(onCall.Phone), null, null, "IT on-call"),
                        new Dictionary<string, string> { ["server"] = server, ["message"] = warning.Message },
                        onCall.WhatsApp ? [NotificationChannel.Sms, NotificationChannel.WhatsApp] : [NotificationChannel.Sms], RemindAfter);
                }
            }

            await services.GetRequiredService<HospitalDbContext>().SaveChangesAsync(cancellationToken);
            foreach (var warning in fresh)
            {
                _told[Key(warning)] = now;
            }
            logger.LogWarning("System health is critical: {Warnings}", string.Join("; ", fresh.Select(w => w.Code)));
            return fresh;
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

    private static string Key(HealthWarning warning) => $"{warning.Code}|{warning.Server}";
}
