using System.Globalization;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features;
using HyFib.HealthCare360.Modules.Engagement.Features.Referrals;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Referring doctors and referral analytics (CRM-05). Visits record the referrer as free text, so the distinct texts
/// of the period are grouped in SQL (typically a few hundred rows), attributed to doctors in memory with
/// <see cref="ReferralAttribution"/>, and only then used to fetch the matching visits for breakdowns.
/// </summary>
public sealed class ReferralService(HospitalDbContext db)
{
    public async Task<IReadOnlyList<ReferrerNames>> DoctorNamesAsync(CancellationToken ct)
    {
        var doctors = await db.Set<ReferringDoctor>().AsNoTracking().Select(d => new { d.Id, d.Name, d.AlternateNames }).ToListAsync(ct);
        return doctors.Select(d => new ReferrerNames(d.Id, d.AlternateNames.Prepend(d.Name).ToList())).ToList();
    }

    /// <summary>Distinct "referred by" texts of this facility's (non-cancelled) visits in the period.</summary>
    public async Task<IReadOnlyList<ReferrerText>> ReferrerTextsAsync(ReportPeriod period, CancellationToken ct)
    {
        var since = period.SinceUtc;
        var until = period.UntilUtc;
        return await db.Set<Visit>().AsNoTracking()
            .Where(v => v.ReferredBy != null && v.VisitedAt >= since && v.VisitedAt < until && v.Status != VisitStatus.Cancelled)
            .GroupBy(v => v.ReferredBy!)
            .Select(g => new ReferrerText(g.Key, g.Count(), g.Max(v => v.VisitedAt)))
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyDictionary<Guid, ReferralTally> ByDoctor, IReadOnlyList<ReferrerText> Unmatched)> AttributeAsync(
        ReportPeriod period,
        CancellationToken ct) =>
        ReferralAttribution.Attribute(await DoctorNamesAsync(ct), await ReferrerTextsAsync(period, ct));

    public async Task<Result<ReferralStats>> StatsAsync(Guid doctorId, ReportPeriod period, CancellationToken ct)
    {
        var doctors = await DoctorNamesAsync(ct);
        if (!doctors.Any(d => d.DoctorId == doctorId))
        {
            return Error.NotFound(nameof(ReferringDoctor), doctorId);
        }

        var texts = (await ReferrerTextsAsync(period, ct))
            .Where(t => ReferralAttribution.DoctorFor(t.Text, doctors) == doctorId)
            .Select(t => t.Text)
            .ToList();

        var since = period.SinceUtc;
        var until = period.UntilUtc;
        var visits = texts.Count == 0
            ? []
            : await db.Set<Visit>().AsNoTracking()
                .Where(v => v.ReferredBy != null && texts.Contains(v.ReferredBy) && v.VisitedAt >= since && v.VisitedAt < until
                            && v.Status != VisitStatus.Cancelled)
                .Select(v => new ReferredVisit(v.VisitedAt, v.DepartmentId, v.Type))
                .ToListAsync(ct);

        var departmentIds = visits.Where(v => v.DepartmentId is not null).Select(v => v.DepartmentId!.Value).Distinct().ToList();
        var departments = await db.Set<Department>().AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var byMonth = period.MonthKeys()
            .Select(month => new CountByLabel(month, visits.Count(v => MonthOf(v.VisitedAt) == month)))
            .ToList();
        var byDepartment = visits
            .GroupBy(v => v.DepartmentId is { } id ? departments.GetValueOrDefault(id, "Other") : "Not recorded")
            .Select(g => new CountByLabel(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Label, StringComparer.Ordinal)
            .ToList();
        var byType = visits
            .GroupBy(v => v.Type)
            .OrderBy(g => g.Key)
            .Select(g => new CountByLabel(g.Key.ToString(), g.Count()))
            .ToList();

        return new ReferralStats(doctorId, period.From, period.To, visits.Count, visits.Count == 0 ? null : visits.Max(v => v.VisitedAt),
            byMonth, byDepartment, byType);
    }

    private static string MonthOf(DateTimeOffset instant) =>
        EngagementClock.ToLocal(instant).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private sealed record ReferredVisit(DateTimeOffset VisitedAt, Guid? DepartmentId, VisitType Type);
}
