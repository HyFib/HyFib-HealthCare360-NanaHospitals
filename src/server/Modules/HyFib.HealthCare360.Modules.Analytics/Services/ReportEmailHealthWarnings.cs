using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// System health warns while scheduled report e-mails are failing (BI-04): a report that could not be generated (its
/// delivery failed) or an e-mail the provider refused that the outbox is still retrying, or gave up on, in the last 24
/// hours. The schedule's owner sees the same in its delivery history; System health is where IT looks.
/// </summary>
internal sealed class ReportEmailHealthWarnings(HospitalDbContext db, TimeProvider time) : IHealthWarningSource
{
    public const string Code = "analytics.report_emails_failing";
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow() - Window;
        var emails =
            from d in db.Set<ReportDelivery>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            join s in db.Set<ReportSchedule>().IgnoreQueryFilters().AsNoTracking() on d.ScheduleId equals s.Id
            where d.CreatedAt >= since && s.Destination == ScheduleDestination.Email
            select d;
        var failed = await emails.Where(d => d.Status == DeliveryStatus.Failed).Select(d => d.ReportTitle).ToListAsync(cancellationToken);
        var unsent = await (
                from r in db.Set<ReportDeliveryRecipient>().AsNoTracking()
                join d in emails on r.DeliveryId equals d.Id
                where r.Status == RecipientStatus.Pending && r.Attempts > 0
                orderby r.Attempts descending
                select new { d.ReportTitle, r.Attempts, r.LastError })
            .ToListAsync(cancellationToken);

        var message = Describe(failed, unsent.Select(u => (u.ReportTitle, u.Attempts, (string?)u.LastError)).ToList());
        return message is null ? [] : [new HealthWarning(Code, "warning", message)];
    }

    /// <summary>The warning's words, or null when nothing failed (unit-tested).</summary>
    internal static string? Describe(IReadOnlyList<string> failedReports, IReadOnlyList<(string Report, int Attempts, string? Error)> unsent)
    {
        if (failedReports.Count == 0 && unsent.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (failedReports.Count > 0)
        {
            var names = failedReports.Distinct(StringComparer.Ordinal).ToList();
            parts.Add($"{failedReports.Count.ToString(CultureInfo.InvariantCulture)} report(s) could not be generated ({string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", …" : string.Empty)})");
        }

        if (unsent.Count > 0)
        {
            var worst = unsent[0];
            parts.Add($"{unsent.Count.ToString(CultureInfo.InvariantCulture)} e-mail(s) are not sent yet after {worst.Attempts.ToString(CultureInfo.InvariantCulture)} attempt(s)" +
                      (string.IsNullOrWhiteSpace(worst.Error) ? string.Empty : $" (last error: {worst.Error})"));
        }

        return $"Scheduled report e-mails in the last 24 hours: {string.Join("; ", parts)}. Failed sends are retried; check the e-mail settings and Analytics › Scheduled e-mails › Delivery history.";
    }
}
