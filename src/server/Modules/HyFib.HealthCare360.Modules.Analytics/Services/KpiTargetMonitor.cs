using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Checks every facility's KPI targets against this month to date (hourly, from <see cref="ScheduledReportJob"/>) and
/// tells the target's owner role when a KPI gets worse: green to amber, or anything to red. The status of each check is
/// kept on the target, so a KPI that stays red is reported once, and a new month starts afresh.
/// </summary>
internal sealed class KpiTargetMonitor(HospitalDbContext db, KpiService kpis, INotificationService notifications, ILogger<KpiTargetMonitor> logger)
{
    public const string Link = "/analytics";

    /// <summary>Checks all targets; returns how many owners' notifications were sent (one per KPI that got worse).</summary>
    public async Task<int> CheckAsync(DateTimeOffset now, CancellationToken ct)
    {
        var targets = await db.Set<KpiTarget>().IgnoreQueryFilters().Where(t => !t.IsDeleted).ToListAsync(ct);
        var alerts = 0;
        foreach (var facility in targets.GroupBy(t => t.FacilityId))
        {
            IReadOnlyDictionary<string, decimal?> values;
            DateOnly from, to;
            try
            {
                (from, to, values) = await kpis.MonthToDateAsync(facility.Key, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The KPI targets of facility {FacilityId} could not be checked; they are checked again within the hour", facility.Key);
                continue;
            }

            var days = AnalyticsClock.Days(from, to);
            foreach (var target in facility)
            {
                if (KpiDefinitions.All.FirstOrDefault(k => k.Key == target.KpiKey) is not { } kpi)
                {
                    continue;
                }

                var value = values.GetValueOrDefault(kpi.Key);
                var view = KpiService.TargetView(kpi.Unit, kpi.HigherIsBetter, value, target, days);

                // The last status counts only within the same month: a new month's figures start from nothing.
                var previous = target.LastCheckedAt is { } last && AnalyticsClock.LocalDate(last) is var lastDay && lastDay.Year == to.Year && lastDay.Month == to.Month
                    ? target.LastStatus
                    : null;
                if (KpiTargetMath.IsNewBreach(previous, view.Status))
                {
                    await NotifyOwnersAsync(target, kpi, value, view, ct);
                    alerts++;
                }

                target.LastStatus = view.Status;
                target.LastValue = value is { } v ? Math.Round(v, 2, MidpointRounding.AwayFromZero) : null;
                target.LastCheckedAt = now;
            }
        }

        if (targets.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return alerts;
    }

    private async Task NotifyOwnersAsync(KpiTarget target, KpiDefinition kpi, decimal? value, KpiTargetView view, CancellationToken ct)
    {
        var role = target.OwnerRole;
        var facilityId = target.FacilityId;
        var owners = await db.Set<User>().AsNoTracking()
            .Where(u => u.IsActive && u.Type == UserType.Staff
                        && u.Roles.Any(r => r.Role != null && r.Role.Name == role)
                        && (u.Facilities.Count == 0 || u.Facilities.Any(f => f.FacilityId == facilityId)))
            .Select(u => u.Id)
            .ToListAsync(ct);
        if (owners.Count == 0)
        {
            logger.LogWarning("KPI {Kpi} of facility {FacilityId} is {Status} but no active user holds the owner role {Role}", kpi.Key, facilityId, view.Status, role);
            return;
        }

        var (title, body) = Message(kpi, value, view);
        await notifications.NotifyUsersAsync(owners, title, body, Link, "analytics", view.Status == KpiStatus.Red ? "error" : "warning", ct);
    }

    /// <summary>The notification's words: "Bed occupancy is red" and the figure against the target (unit-tested).</summary>
    internal static (string Title, string Body) Message(KpiDefinition kpi, decimal? value, KpiTargetView view)
    {
        var status = view.Status == KpiStatus.Red ? "red" : "amber";
        var basis = view.Prorated ? " (the monthly target prorated to the month so far)" : string.Empty;
        var limit = view.Status == KpiStatus.Red ? view.Red : view.Amber;
        var title = $"{kpi.Label} is {status}";
        var body = $"Month to date: {Format(kpi.Unit, value)} against a target of {Format(kpi.Unit, view.Target)}{basis}; {status} at {Format(kpi.Unit, limit)} or {(kpi.HigherIsBetter ? "below" : "above")}.";
        return (title, body);
    }

    internal static string Format(string unit, decimal? value) => value is not { } v
        ? "no figure"
        : unit switch
        {
            "inr" => "₹" + v.ToString("N2", CultureInfo.GetCultureInfo("en-IN")),
            "percent" => v.ToString("0.#", CultureInfo.InvariantCulture) + "%",
            "days" => v.ToString("0.#", CultureInfo.InvariantCulture) + " days",
            "minutes" => v.ToString("0.#", CultureInfo.InvariantCulture) + " min",
            "count" => v.ToString("N0", CultureInfo.GetCultureInfo("en-IN")),
            _ => v.ToString("0.#", CultureInfo.InvariantCulture),
        };
}
