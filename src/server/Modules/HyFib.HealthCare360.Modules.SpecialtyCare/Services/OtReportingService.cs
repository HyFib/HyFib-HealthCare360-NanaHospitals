using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>OT calendar by theatre/day/week or by surgeon (OT-01) and utilisation metrics (OT-07).</summary>
public sealed class OtReportingService(HospitalDbContext db, TimeProvider time)
{
    /// <summary>Default planned OT session per theatre per day (8 hours) used as the utilisation denominator.</summary>
    public const int DefaultMinutesPerDay = 480;

    public async Task<OtCalendar> CalendarAsync(DateOnly from, DateOnly to, Guid? theatreId, Guid? surgeonId, CancellationToken ct)
    {
        var (start, end) = SpecialtyClock.RangeUtc(from, to);
        var theatres = await db.Set<OperationTheatre>().AsNoTracking()
            .Where(t => t.IsActive && (theatreId == null || t.Id == theatreId))
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Code)
            .Select(t => new { t.Id, t.Code, t.Name, t.Type })
            .ToListAsync(ct);

        var bookings = db.Set<SurgeryBooking>().AsNoTracking().Where(b => surgeonId == null || b.SurgeonId == surgeonId);
        var scheduled = bookings.Where(b => b.TheatreId != null && b.ScheduledStart < end && b.ScheduledEnd > start
                                            && b.Status != SurgeryStatus.Cancelled && b.Status != SurgeryStatus.Postponed && b.Status != SurgeryStatus.Requested);
        var awaiting = bookings.Where(b => (b.Status == SurgeryStatus.Requested || b.Status == SurgeryStatus.Postponed)
                                           && (b.PreferredDate == null || (b.PreferredDate >= from && b.PreferredDate <= to)));

        var today = time.Today();
        var rows = (await ProjectAsync(scheduled, ct)).Concat(await ProjectAsync(awaiting, ct)).ToList();
        var names = await SpecialtyLookups.PractitionerNamesAsync(db, rows.SelectMany(r => new[] { r.SurgeonId, r.AnaesthetistId }), ct);
        var items = rows.Select(r => new OtCalendarBooking(
                r.Id, r.BookingNumber, r.PatientId, r.Uhid, r.FullName, r.Gender, r.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
                r.ProcedureName, r.Laterality, r.Priority, r.Status, r.TheatreId, r.ScheduledStart, r.ScheduledEnd, r.SurgeonId,
                r.SurgeonId is { } s ? names.GetValueOrDefault(s) : null, r.AnaesthetistId is { } a ? names.GetValueOrDefault(a) : null,
                r.AnaesthesiaType, r.SignInDone, r.TimeOutDone, r.SignOutDone))
            .ToList();

        var onBoard = items.Where(i => i.Status is not (SurgeryStatus.Requested or SurgeryStatus.Postponed)).ToList();
        return new OtCalendar(
            from,
            to,
            theatres.Select(t => new OtCalendarTheatre(t.Id, t.Code, t.Name, t.Type,
                onBoard.Where(i => i.TheatreId == t.Id).OrderBy(i => i.Start).ToList())).ToList(),
            items.Where(i => i.Status is SurgeryStatus.Requested or SurgeryStatus.Postponed).OrderByDescending(i => i.Priority).ThenBy(i => i.BookingNumber).ToList());
    }

    public async Task<OtUtilisationReport> UtilisationAsync(DateOnly from, DateOnly to, Guid? theatreId, int minutesPerDay, CancellationToken ct)
    {
        var (start, end) = SpecialtyClock.RangeUtc(from, to);
        var rows = await db.Set<SurgeryBooking>().AsNoTracking()
            .Where(b => b.TheatreId != null && b.ScheduledStart >= start && b.ScheduledStart < end && b.ScheduledEnd != null
                        && (theatreId == null || b.TheatreId == theatreId))
            .Select(b => new { TheatreId = b.TheatreId!.Value, b.Status, Start = b.ScheduledStart!.Value, End = b.ScheduledEnd!.Value, b.InRoomAt, b.StartedAt, b.CompletedAt })
            .ToListAsync(ct);

        var cases = rows.Select(r => new OtCaseTiming(r.TheatreId, SpecialtyClock.ToLocalDate(r.Start), r.Status, r.Start, r.End, r.InRoomAt ?? r.StartedAt, r.CompletedAt))
            .ToList();
        var theatreIds = cases.Select(c => c.TheatreId).ToHashSet();
        var theatres = await db.Set<OperationTheatre>().AsNoTracking()
            .Where(t => (theatreId == null || t.Id == theatreId) && (t.IsActive || theatreIds.Contains(t.Id)))
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Code)
            .Select(t => new { t.Id, t.Code, t.Name })
            .ToListAsync(ct);

        var available = (to.DayNumber - from.DayNumber + 1) * minutesPerDay;
        return new OtUtilisationReport(from, to, minutesPerDay,
            theatres.Select(t => new OtUtilisationRow(t.Id, t.Code, t.Name, OtUtilisation.Calculate(t.Id, cases, available))).ToList());
    }

    private async Task<List<CalendarRow>> ProjectAsync(IQueryable<SurgeryBooking> bookings, CancellationToken ct) =>
        await (from b in bookings
               join p in db.Set<Patient>() on b.PatientId equals p.Id
               select new CalendarRow(
                   b.Id, b.BookingNumber, b.PatientId, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, b.ProcedureName, b.Laterality, b.Priority, b.Status,
                   b.TheatreId, b.ScheduledStart, b.ScheduledEnd, b.SurgeonId, b.AnaesthetistId, b.AnaesthesiaType,
                   b.SignInCompletedAt != null, b.TimeOutCompletedAt != null, b.SignOutCompletedAt != null))
            .ToListAsync(ct);

    private sealed record CalendarRow(
        Guid Id, string BookingNumber, Guid PatientId, string Uhid, string FullName, Gender Gender, DateOnly? DateOfBirth, string ProcedureName,
        Laterality Laterality, SurgeryPriority Priority, SurgeryStatus Status, Guid? TheatreId, DateTimeOffset? ScheduledStart, DateTimeOffset? ScheduledEnd,
        Guid? SurgeonId, Guid? AnaesthetistId, AnaesthesiaType AnaesthesiaType, bool SignInDone, bool TimeOutDone, bool SignOutDone);
}
