using System.Globalization;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Daily biomedical reminder (AST-03/05): after 07:00 IST, holders of <c>assets.maintenance</c> in each facility get
/// one in-app summary of PM visits and calibrations due within a week (or overdue) and warranties / AMC-CMC
/// contracts lapsing within 30 days. Runs only on job-running nodes; the last run date is persisted so a restart
/// never sends the same day's reminder twice.
/// </summary>
internal sealed class AssetReminderJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<AssetReminderJob> logger) : BackgroundService
{
    internal const string LastRunSetting = "supplychain.assets.reminders.last_run";
    private const int RunAfterHour = 7;
    private DateOnly? _lastRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30), time);
        do
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Asset maintenance reminders failed; will retry on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunIfDueAsync(CancellationToken ct)
    {
        var now = HospitalClock.LocalNow(time);
        var today = DateOnly.FromDateTime(now.DateTime);
        if (now.Hour < RunAfterHour || _lastRun == today)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (await settings.GetAsync<DateOnly?>(LastRunSetting, null, ct) == today)
        {
            _lastRun = today;
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var due = await MaintenanceService.DueItemsAsync(db, today, MaintenanceService.DueWindowDays, MaintenanceService.ExpiryWindowDays, ct);

        foreach (var facility in due.GroupBy(d => d.FacilityId))
        {
            var items = facility.ToList();
            var overdue = items.Count(i => i.DaysToDue < 0 && i.Kind is MaintenanceService.PmKind or MaintenanceService.CalibrationKind);
            var body = string.Join("; ", items.GroupBy(i => i.Kind).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key}: {g.Count()}")))
                       + (overdue > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({overdue} overdue)") : string.Empty)
                       + ". Next: " + string.Join(", ", items.Take(3).Select(i => $"{i.AssetName} {i.Title.ToLowerInvariant()} {i.DueDate.ToString("dd-MMM", CultureInfo.InvariantCulture)}"));
            await notifications.NotifyPermissionHoldersAsync(
                SupplyChainPermissions.AssetsMaintenance,
                facility.Key,
                string.Create(CultureInfo.InvariantCulture, $"{items.Count} equipment maintenance item(s) need attention"),
                body,
                "/assets/maintenance",
                "assets",
                overdue > 0 ? "warning" : "info",
                ct);
        }

        await settings.SetAsync(LastRunSetting, today, null, false, ct);
        _lastRun = today;
        logger.LogInformation("Asset maintenance reminders sent for {Count} item(s)", due.Count);
    }
}
