using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>Trend charts, adherence and the monitoring dashboard (RPM-04). Days are IST calendar days.</summary>
public sealed class RpmInsights(HospitalDbContext db, VirtualCareSettings settings, TimeProvider time)
{
    public const int MaxPointsPerSeries = 2000;
    private readonly HospitalCalendar _calendar = HospitalCalendar.India;

    public async Task<IReadOnlyList<RpmTrend>> TrendsAsync(RpmEnrollment enrollment, IReadOnlyCollection<RpmReadingType> types, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var start = _calendar.StartOfDay(from);
        var end = _calendar.StartOfDay(to.AddDays(1));
        var readings = db.Set<RpmReading>().AsNoTracking()
            .Where(r => r.EnrollmentId == enrollment.Id && r.MeasuredAt >= start && r.MeasuredAt < end);
        if (types.Count > 0)
        {
            readings = readings.Where(r => types.Contains(r.Type));
        }

        var rows = await readings
            .OrderBy(r => r.MeasuredAt)
            .Select(r => new { r.Type, r.Value, r.MeasuredAt, r.Severity })
            .Take(20_000)
            .ToListAsync(ct);
        var limits = await db.Set<RpmThreshold>().AsNoTracking()
            .Where(t => t.EnrollmentId == enrollment.Id)
            .ToDictionaryAsync(t => t.Type, t => t.Range, ct);

        return rows
            .GroupBy(r => r.Type)
            .OrderBy(g => g.Key)
            .Select(g => new RpmTrend(
                g.Key,
                ReadingUnits.Label(g.Key),
                ReadingUnits.CanonicalUnit(g.Key),
                limits.TryGetValue(g.Key, out var range) ? ThresholdDto.From(g.Key, range) : null,
                g.TakeLast(MaxPointsPerSeries).Select(r => new TrendPoint(r.MeasuredAt, r.Value, r.Severity)).ToList(),
                g.GroupBy(r => _calendar.DateOf(r.MeasuredAt))
                    .Select(d => new TrendDay(d.Key, d.Min(r => r.Value), d.Max(r => r.Value), Math.Round(d.Average(r => r.Value), 1, MidpointRounding.AwayFromZero), d.Count()))
                    .ToList()))
            .ToList();
    }

    public async Task<RpmAdherenceDto> AdherenceAsync(RpmEnrollment enrollment, int days, CancellationToken ct)
    {
        var today = time.Today();
        var window = Math.Clamp(days, 1, 365);
        var since = _calendar.StartOfDay(today.AddDays(-(window - 1)));
        var instants = await db.Set<RpmReading>().AsNoTracking()
            .Where(r => r.EnrollmentId == enrollment.Id && r.MeasuredAt >= since)
            .Select(r => r.MeasuredAt)
            .ToListAsync(ct);
        var dates = instants.Select(_calendar.DateOf).ToList();
        var summary = Adherence.Compute(dates, today, enrollment.StartDate, window, enrollment.IsClosed ? enrollment.EndDate : null);
        var expected = await db.Set<RpmProgram>().AsNoTracking().Where(p => p.Id == enrollment.ProgramId).Select(p => p.ExpectedReadingsPerDay).FirstOrDefaultAsync(ct);
        var perDay = dates.GroupBy(d => d).OrderBy(g => g.Key).Select(g => new DayCount(g.Key, g.Count())).ToList();
        return new RpmAdherenceDto(summary.WindowDays, summary.DaysWithReadings, summary.Percent, summary.MissedDates, Math.Max(expected, 1), perDay);
    }

    /// <summary>Counts for the current facility (the query filter scopes enrolments, devices, readings and alerts).</summary>
    public async Task<RpmDashboard> DashboardAsync(Guid? facilityId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var todayStart = _calendar.StartOfDay(time.Today());
        var enrollments = await db.Set<RpmEnrollment>().AsNoTracking()
            .Where(e => e.Status == RpmEnrollmentStatus.Active || e.Status == RpmEnrollmentStatus.Paused)
            .Select(e => new { e.Status, e.LastReadingAt })
            .ToListAsync(ct);
        var alerts = await db.Set<RpmAlert>().AsNoTracking()
            .Where(a => a.Status == RpmAlertStatus.Open || a.Status == RpmAlertStatus.Acknowledged)
            .Select(a => new { a.Status, a.Severity })
            .ToListAsync(ct);
        var silence = await settings.DeviceSilenceAsync(facilityId, ct);
        var silentBefore = now - silence;
        var silentDevices = await db.Set<RpmDevice>().AsNoTracking()
            .CountAsync(d => d.IsActive && d.EnrollmentId != null && (d.LastSeenAt ?? d.AssignedAt) < silentBefore, ct);
        var dayAgo = now.AddHours(-24);
        var readingsLastDay = await db.Set<RpmReading>().AsNoTracking().CountAsync(r => r.ReceivedAt >= dayAgo, ct);

        return new RpmDashboard(
            enrollments.Count(e => e.Status == RpmEnrollmentStatus.Active),
            enrollments.Count(e => e.Status == RpmEnrollmentStatus.Paused),
            alerts.Count,
            alerts.Count(a => a.Severity == RpmAlertSeverity.Critical),
            alerts.Count(a => a.Severity == RpmAlertSeverity.Critical && a.Status == RpmAlertStatus.Open),
            silentDevices,
            readingsLastDay,
            enrollments.Count(e => e.Status == RpmEnrollmentStatus.Active && (e.LastReadingAt is null || e.LastReadingAt < todayStart)));
    }
}
