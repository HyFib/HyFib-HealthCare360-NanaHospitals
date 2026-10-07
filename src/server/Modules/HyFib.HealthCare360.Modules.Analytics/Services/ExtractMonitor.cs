using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Reports.Export;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Data-refresh monitoring of BI extracts (BI-05). The refresh log in the extract folder lists every extract of every
/// facility with when it was last refreshed, so a Power BI report can show "data as of" beside its visuals; and the
/// people who manage schedules at the facility are told when an extract stops refreshing (once, not at every failed
/// run). The extracts screen shows the same state (<see cref="ReportSchedule.State"/>).
/// </summary>
internal sealed class ExtractMonitor(
    HospitalDbContext db,
    ExtractFolder folder,
    ReportCatalog catalog,
    INotificationService notifications,
    TimeProvider time,
    ILogger<ExtractMonitor> logger)
{
    public const string LogLink = "/analytics/extracts";

    /// <summary>Column headings of the refresh log.</summary>
    public static readonly string[] LogColumns = ["Facility code", "Extract", "File", "Report", "Refresh", "Last refreshed (IST)", "State", "Problem"];

    /// <summary>
    /// Rewrites the refresh log from the extracts as saved. It is written after each extract run; a log that cannot be
    /// written is logged and does not fail the run (the extract itself was written).
    /// </summary>
    public async Task WriteRefreshLogAsync(CancellationToken ct)
    {
        try
        {
            var extracts = await db.Set<ReportSchedule>().AsNoTracking()
                .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(s => s.Destination == ScheduleDestination.ExtractFolder && s.ExtractName != null)
                .ToListAsync(ct);
            var facilityIds = extracts.Select(e => e.FacilityId).Distinct().ToList();
            var codes = await db.Set<Facility>().AsNoTracking()
                .Where(f => facilityIds.Contains(f.Id))
                .ToDictionaryAsync(f => f.Id, f => f.Code, ct);

            var now = time.GetUtcNow();
            var csv = new StringBuilder();
            ReportExporter.Line(csv, LogColumns);
            foreach (var extract in extracts
                         .Where(e => codes.ContainsKey(e.FacilityId))
                         .OrderBy(e => codes[e.FacilityId], StringComparer.Ordinal)
                         .ThenBy(e => e.ExtractName, StringComparer.Ordinal))
            {
                var code = codes[extract.FacilityId];
                ReportExporter.Line(
                    csv,
                    code,
                    extract.Name,
                    Path.Combine(ExtractFolder.FolderNameFor(code), ExtractFolder.FileName(extract.ExtractName!)),
                    catalog.Find(extract.ReportId)?.Descriptor.Title ?? extract.ReportId,
                    extract.Describe(),
                    extract.LastSucceededAt is { } at ? AnalyticsClock.ToLocal(at).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : null,
                    extract.State(now).ToString(),
                    extract.IsFailing ? extract.LastError : null);
            }

            await folder.WriteAsync(Path.Combine(folder.Root, ExtractFolder.RefreshLogFile), [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())], ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The BI extract refresh log could not be written to {Folder}", folder.Root);
        }
    }

    /// <summary>
    /// Tells the facility's schedule managers that <paramref name="extract"/> was not refreshed — when it starts failing,
    /// not again at each run while it keeps failing (<paramref name="wasFailing"/>).
    /// </summary>
    public async Task FailedAsync(ReportSchedule extract, bool wasFailing, string problem, CancellationToken ct)
    {
        if (wasFailing || !extract.IsExtract)
        {
            return;
        }

        await notifications.NotifyPermissionHoldersAsync(
            AnalyticsPermissions.SchedulesManage,
            extract.FacilityId,
            $"BI extract \"{extract.Name}\" was not refreshed",
            $"{problem} BI tools keep reading the last file until a refresh succeeds.",
            LogLink,
            "analytics",
            "warning",
            ct);
    }
}
