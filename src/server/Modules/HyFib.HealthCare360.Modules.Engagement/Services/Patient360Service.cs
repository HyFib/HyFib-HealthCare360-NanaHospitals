using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features;
using HyFib.HealthCare360.Modules.Engagement.Features.Patient360;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

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
