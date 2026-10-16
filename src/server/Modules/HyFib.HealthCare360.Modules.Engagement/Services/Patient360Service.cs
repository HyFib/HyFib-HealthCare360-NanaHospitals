using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features;
using HyFib.HealthCare360.Modules.Engagement.Features.Patient360;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Assembles the patient 360° view (CRM-04) from small, indexed queries. Visits, feedback, complaints, follow-ups and
/// campaigns are limited to the viewer's facility. Spend covers the whole group, because Billing's events carry no
/// facility. Callers must enforce restricted-record access and audit the view.
/// </summary>
public sealed class Patient360Service(HospitalDbContext db, CommunicationConsentService consents, TimeProvider time)
{
    private const int TopDepartments = 5;
    private const int RecentComplaints = 5;

    public async Task<Result<Patient360View>> GetAsync(Guid patientId, CancellationToken ct)
    {
        var p = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == patientId, ct);
        if (p is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        var today = time.Today();
        var yearAgo = EngagementClock.StartOfDayUtc(today.AddYears(-1));
        var identity = new Patient360Identity(
            p.Id, p.Uhid, p.FullName, p.Gender, p.AgeDisplay(today), p.Phone is null ? null : Phone.Mask(p.Phone), p.Address.City, p.Category,
            p.PreferredLanguage, p.IsVip, p.IsDeceased, p.RegisteredAt);

        return new Patient360View(
            identity,
            await VisitsAsync(patientId, yearAgo, ct),
            await SpendAsync(patientId, yearAgo, ct),
            await FeedbackAsync(patientId, ct),
            await db.Set<Complaint>().AsNoTracking()
                .Where(c => c.PatientId == patientId)
                .OrderByDescending(c => c.RaisedAt)
                .Take(RecentComplaints)
                .Select(c => new ComplaintBrief(c.Id, c.ComplaintNumber, c.Status, c.Priority, c.Category, c.RaisedAt))
                .ToListAsync(ct),
            (await db.Set<FollowUpTask>().AsNoTracking()
                    .Where(t => t.PatientId == patientId && (t.Status == FollowUpStatus.Open || t.Status == FollowUpStatus.InProgress))
                    .OrderBy(t => t.DueDate)
                    .ToListAsync(ct))
                .Select(t => new FollowUpBrief(t.Id, t.Type, t.DueDate, t.Status, t.Priority, t.IsOverdue(today)))
                .ToList(),
            await CommunicationAsync(patientId, ct),
            await db.Set<Lead>().AsNoTracking()
                .Where(l => l.ConvertedPatientId == patientId)
                .OrderByDescending(l => l.ConvertedAt)
                .Select(l => new LeadBrief(l.Id, l.LeadNumber, l.Source, l.SourceDetail, l.CreatedAt, l.ConvertedAt))
                .FirstOrDefaultAsync(ct));
    }

    private async Task<VisitSummary> VisitsAsync(Guid patientId, DateTimeOffset yearAgo, CancellationToken ct)
    {
        var visits = await db.Set<Visit>().AsNoTracking()
            .Where(v => v.PatientId == patientId && v.Status != VisitStatus.Cancelled)
            .Select(v => new { v.VisitedAt, v.Type, v.DepartmentId, v.ReferredBy })
            .ToListAsync(ct);
        var departmentIds = visits.Where(v => v.DepartmentId is not null).Select(v => v.DepartmentId!.Value).Distinct().ToList();
        var departments = await db.Set<Department>().AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return new VisitSummary(
            visits.Count,
            visits.Count(v => v.VisitedAt >= yearAgo),
            visits.Count == 0 ? null : visits.Min(v => v.VisitedAt),
            visits.Count == 0 ? null : visits.Max(v => v.VisitedAt),
            visits.Where(v => !string.IsNullOrWhiteSpace(v.ReferredBy)).OrderByDescending(v => v.VisitedAt).Select(v => v.ReferredBy).FirstOrDefault(),
            visits.GroupBy(v => v.Type).OrderBy(g => g.Key).Select(g => new CountByLabel(g.Key.ToString(), g.Count())).ToList(),
            visits.Where(v => v.DepartmentId is not null)
                .GroupBy(v => departments.GetValueOrDefault(v.DepartmentId!.Value, "Other"))
                .Select(g => new CountByLabel(g.Key, g.Count()))
                .OrderByDescending(c => c.Count).ThenBy(c => c.Label, StringComparer.Ordinal)
                .Take(TopDepartments)
                .ToList());
    }

    private async Task<SpendSummary> SpendAsync(Guid patientId, DateTimeOffset yearAgo, CancellationToken ct)
    {
        var entries = await db.Set<PatientSpendEntry>().AsNoTracking()
            .Where(e => e.PatientId == patientId)
            .Select(e => new SpendRow(e.Kind, e.SourceId, e.Amount, e.OccurredAt))
            .ToListAsync(ct);
        return Summarize(entries, yearAgo);
    }

    internal sealed record SpendRow(SpendKind Kind, Guid SourceId, decimal Amount, DateTimeOffset OccurredAt);

    /// <summary>Net spend: cancelled bills drop out entirely and refunds paid out reduce what the patient has paid.</summary>
    internal static SpendSummary Summarize(IReadOnlyCollection<SpendRow> entries, DateTimeOffset yearAgo)
    {
        var cancelled = entries.Where(e => e.Kind == SpendKind.BillCancelled).Select(e => e.SourceId).ToHashSet();
        var bills = entries.Where(e => e.Kind == SpendKind.Billed && !cancelled.Contains(e.SourceId)).ToList();
        var paid = entries.Where(e => e.Kind == SpendKind.Paid).Sum(e => e.Amount) - entries.Where(e => e.Kind == SpendKind.Refunded).Sum(e => e.Amount);
        return new SpendSummary(
            bills.Sum(e => e.Amount),
            paid,
            bills.Where(e => e.OccurredAt >= yearAgo).Sum(e => e.Amount),
            bills.Count,
            bills.Count == 0 ? null : bills.Max(e => e.OccurredAt));
    }

    private async Task<FeedbackSummary> FeedbackAsync(Guid patientId, CancellationToken ct)
    {
        var responses = await db.Set<FeedbackResponse>().AsNoTracking()
            .Where(r => r.PatientId == patientId)
            .OrderByDescending(r => r.SubmittedAt)
            .ToListAsync(ct);
        var latest = responses.FirstOrDefault();
        var averages = responses.Select(r => r.Ratings.Average).OfType<decimal>().ToList();
        return new FeedbackSummary(
            responses.Count,
            averages.Count == 0 ? null : Math.Round(averages.Average(), 2, MidpointRounding.AwayFromZero),
            responses.FirstOrDefault(r => r.Nps is not null)?.Nps,
            latest?.Sentiment,
            responses.Count(r => r.IsLowScore),
            latest?.SubmittedAt);
    }

    /// <summary>
    /// The patient's communications history (CRM-04), newest first: service messages and campaign messages from the
    /// notification log, campaigns that passed the patient over, and follow-up call tasks that have been called on.
    /// Messages are limited to the viewer's facility (and group-wide ones), like the rest of the view. The caller checks
    /// that the patient may be opened.
    /// </summary>
    public async Task<PagedResult<CommunicationEntry>> CommunicationsAsync(Guid patientId, CommunicationKind? kind, PageRequest page, CancellationToken ct)
    {
        // Each source is read newest first only as far as the end of the requested page and the rows merged here: the
        // page can only come from those rows, and one patient's history is short enough for this to stay small.
        var depth = page.Skip + page.Take;
        var facility = db.CurrentFacilityId;
        var entries = new List<CommunicationEntry>();
        long total = 0;

        if (kind is null or CommunicationKind.Message or CommunicationKind.Campaign)
        {
            var campaignLogIds = db.Set<CampaignRecipient>()
                .Where(r => r.PatientId == patientId && r.NotificationLogId != null)
                .Select(r => r.NotificationLogId!.Value);
            var logs = db.Set<NotificationLog>().AsNoTracking()
                .Where(l => l.PatientId == patientId && (facility == null || l.FacilityId == null || l.FacilityId == facility));
            logs = kind switch
            {
                CommunicationKind.Message => logs.Where(l => !campaignLogIds.Contains(l.Id)),
                CommunicationKind.Campaign => logs.Where(l => campaignLogIds.Contains(l.Id)),
                _ => logs,
            };

            total += await logs.LongCountAsync(ct);
            entries.AddRange(await LogEntriesAsync(
                await logs.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).Take(depth).ToListAsync(ct), ct));
        }

        if (kind is null or CommunicationKind.Campaign)
        {
            // A campaign the patient was skipped by sent nothing, so it has no log; it still says why nothing came.
            var skipped =
                from r in db.Set<CampaignRecipient>().AsNoTracking()
                join c in db.Set<Campaign>() on r.CampaignId equals c.Id
                where r.PatientId == patientId && r.NotificationLogId == null
                select new { r.Id, r.CreatedAt, r.SkipReason, CampaignId = c.Id, c.Name, c.TemplateKey, c.Channel };
            total += await skipped.LongCountAsync(ct);
            entries.AddRange((await skipped.OrderByDescending(r => r.CreatedAt).Take(depth).ToListAsync(ct))
                .Select(r => new CommunicationEntry(
                    r.Id, CommunicationKind.Campaign, r.CreatedAt, CampaignChannelOf(r.Channel), r.TemplateKey, Purpose: null, Recipient: null,
                    NotificationStatus.Skipped, SentAt: null, DeliveredAt: null, r.CampaignId, r.Name, r.SkipReason,
                    FollowUpId: null, FollowUpType: null, FollowUpStatus: null, CallOutcome: null, CallAttempts: null)));
        }

        if (kind is null or CommunicationKind.FollowUpCall)
        {
            // Tasks closed because the patient visited or booked were never called, so they are not communications.
            var calls = db.Set<FollowUpTask>().AsNoTracking().Where(t => t.PatientId == patientId && t.Attempts > 0);
            total += await calls.LongCountAsync(ct);
            entries.AddRange((await calls
                    .Select(t => new { t.Id, At = t.CompletedAt ?? t.UpdatedAt ?? t.CreatedAt, t.Type, t.Status, t.Outcome, t.Attempts })
                    .OrderByDescending(t => t.At)
                    .Take(depth)
                    .ToListAsync(ct))
                .Select(t => new CommunicationEntry(
                    t.Id, CommunicationKind.FollowUpCall, t.At, Channel: null, TemplateKey: null, Purpose: null, Recipient: null, Status: null,
                    SentAt: null, DeliveredAt: null, CampaignId: null, CampaignName: null, SkipReason: null,
                    t.Id, t.Type, t.Status, t.Outcome, t.Attempts)));
        }

        return Page(entries, total, page);
    }

    /// <summary>The requested page of entries gathered from every source, newest first.</summary>
    internal static PagedResult<CommunicationEntry> Page(IEnumerable<CommunicationEntry> entries, long total, PageRequest page) =>
        new(entries.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Skip(page.Skip).Take(page.Take).ToList(), page.Page, page.Take, total);

    private static NotificationChannel CampaignChannelOf(CampaignChannel channel) =>
        channel == CampaignChannel.WhatsApp ? NotificationChannel.WhatsApp : NotificationChannel.Sms;

    /// <summary>Log rows as entries, with the campaign that sent each one (if any) and what its template is for.</summary>
    private async Task<IEnumerable<CommunicationEntry>> LogEntriesAsync(IReadOnlyList<NotificationLog> logs, CancellationToken ct)
    {
        var logIds = logs.Select(l => (Guid?)l.Id).ToList();
        var campaignOf = await (
                from r in db.Set<CampaignRecipient>().AsNoTracking()
                join c in db.Set<Campaign>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on r.CampaignId equals c.Id
                where logIds.Contains(r.NotificationLogId)
                select new { LogId = r.NotificationLogId!.Value, c.Id, c.Name })
            .ToDictionaryAsync(x => x.LogId, ct);

        // A facility's own template wins over the group's, as it does when the message is sent.
        var keys = logs.Select(l => l.TemplateKey).Distinct().ToList();
        var facility = db.CurrentFacilityId;
        var purposes = (await db.Set<NotificationTemplate>().AsNoTracking()
                .Where(t => keys.Contains(t.Key) && t.Description != null && (t.FacilityId == null || t.FacilityId == facility))
                .Select(t => new { t.Key, t.Channel, t.Language, t.FacilityId, t.Description })
                .ToListAsync(ct))
            .OrderBy(t => t.FacilityId is null).ThenBy(t => t.Language != "en")
            .GroupBy(t => (t.Key, t.Channel))
            .ToDictionary(g => g.Key, g => g.First().Description);

        return logs.Select(l =>
        {
            var campaign = campaignOf.GetValueOrDefault(l.Id);
            return new CommunicationEntry(
                l.Id, campaign is null ? CommunicationKind.Message : CommunicationKind.Campaign, l.CreatedAt, l.Channel, l.TemplateKey,
                purposes.GetValueOrDefault((l.TemplateKey, l.Channel)), l.Recipient, l.Status, l.SentAt, l.DeliveredAt,
                campaign?.Id, campaign?.Name, SkipReason: null, FollowUpId: null, FollowUpType: null, FollowUpStatus: null, CallOutcome: null,
                CallAttempts: null);
        });
    }

    private async Task<CommunicationSummary> CommunicationAsync(Guid patientId, CancellationToken ct)
    {
        var preferences = await consents.PreferencesAsync(patientId, ct);
        var campaigns = await (
                from r in db.Set<CampaignRecipient>().AsNoTracking()
                join c in db.Set<Campaign>() on r.CampaignId equals c.Id
                where r.PatientId == patientId && r.Status == CampaignRecipientStatus.Queued
                select new { r.CreatedAt, r.OptedOutAt })
            .ToListAsync(ct);
        return new CommunicationSummary(
            preferences.Marketing,
            preferences.Updates,
            preferences.MarketingChangedAt,
            campaigns.Count,
            campaigns.Count == 0 ? null : campaigns.Max(c => c.CreatedAt),
            campaigns.Any(c => c.OptedOutAt is not null));
    }
}
