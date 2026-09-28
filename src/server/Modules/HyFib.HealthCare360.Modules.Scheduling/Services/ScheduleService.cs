using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Schedules;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// Maintains doctor OPD sessions and their dated exceptions (PMS-01). A doctor may never have two sessions that
/// overlap in time (at any branch). Changes that strand existing bookings are allowed — the doctor's leave is a
/// fact — but the affected appointments are returned so the front desk can reschedule and inform the patients.
/// </summary>
public sealed class ScheduleService(HospitalDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    private readonly HospitalCalendar _calendar = HospitalCalendar.India;

    public async Task<Result<SchedulesCreated>> CreateAsync(CreateSchedulesRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var check = await CheckReferencesAsync(request.PractitionerId, request.DepartmentId, ct);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var schedules = request.Weekdays.Distinct().Select(day => new DoctorSchedule
        {
            FacilityId = facilityId,
            PractitionerId = request.PractitionerId,
            DepartmentId = request.DepartmentId,
            Weekday = day,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            SlotMinutes = request.SlotMinutes,
            CapacityPerSlot = request.CapacityPerSlot,
            Mode = request.Mode,
            Room = Clean(request.Room),
            EffectiveFrom = request.EffectiveFrom,
            EffectiveTo = request.EffectiveTo,
            Notes = Clean(request.Notes),
        }).ToList();

        var overlap = await FindOverlapAsync(schedules, ct);
        if (overlap is not null)
        {
            return overlap;
        }

        db.AddRange(schedules);
        await db.SaveChangesAsync(ct);
        return new SchedulesCreated(schedules.Select(s => s.Id).ToList());
    }

    public async Task<Result<ScheduleSaved>> UpdateAsync(Guid id, UpdateScheduleRequest request, CancellationToken ct)
    {
        var schedule = await db.Set<DoctorSchedule>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return Error.NotFound(nameof(DoctorSchedule), id);
        }

        var check = await CheckReferencesAsync(schedule.PractitionerId, request.DepartmentId, ct);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        db.SetExpectedVersion(schedule, request.Version);
        schedule.DepartmentId = request.DepartmentId;
        schedule.Weekday = request.Weekday;
        schedule.StartTime = request.StartTime;
        schedule.EndTime = request.EndTime;
        schedule.SlotMinutes = request.SlotMinutes;
        schedule.CapacityPerSlot = request.CapacityPerSlot;
        schedule.Mode = request.Mode;
        schedule.Room = Clean(request.Room);
        schedule.EffectiveFrom = request.EffectiveFrom;
        schedule.EffectiveTo = request.EffectiveTo;
        schedule.IsActive = request.IsActive;
        schedule.Notes = Clean(request.Notes);

        var overlap = await FindOverlapAsync([schedule], ct);
        if (overlap is not null)
        {
            return overlap;
        }

        await db.SaveChangesAsync(ct);
        return new ScheduleSaved(schedule.Id, await StrandedBookingsAsync(schedule, ct));
    }

    public async Task<Result<ScheduleSaved>> DeleteAsync(Guid id, CancellationToken ct)
    {
        var schedule = await db.Set<DoctorSchedule>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return Error.NotFound(nameof(DoctorSchedule), id);
        }

        db.Remove(schedule);
        await db.SaveChangesAsync(ct);
        schedule.IsActive = false;
        return new ScheduleSaved(schedule.Id, await StrandedBookingsAsync(schedule, ct));
    }

    public async Task<Result<ScheduleSaved>> SaveExceptionAsync(Guid? id, SaveScheduleExceptionRequest request, CancellationToken ct)
    {
        if (!await db.Set<Practitioner>().AnyAsync(p => p.Id == request.PractitionerId && p.IsActive, ct))
        {
            return Error.Field("practitionerId", "Choose an active doctor.");
        }

        Guid? facilityId = request.AllFacilities ? null : currentUser.FacilityId;
        if (!request.AllFacilities && facilityId is null)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (request.Type == ScheduleExceptionType.ExtraSession
            && !await db.Set<Department>().AnyAsync(d => d.Id == request.DepartmentId && d.IsActive, ct))
        {
            return Error.Field("departmentId", "Choose an active department.");
        }

        ScheduleException exception;
        if (id is { } existingId)
        {
            var found = await db.Set<ScheduleException>().FirstOrDefaultAsync(e => e.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(ScheduleException), existingId);
            }

            if (!BelongsToCurrentFacility(found))
            {
                return OtherBranch;
            }

            exception = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(exception, version);
            }
        }
        else
        {
            exception = new ScheduleException();
            db.Add(exception);
        }

        var isExtra = request.Type == ScheduleExceptionType.ExtraSession;
        exception.PractitionerId = request.PractitionerId;
        exception.FacilityId = facilityId;
        exception.Type = request.Type;
        exception.FromDate = request.FromDate;
        exception.ToDate = request.ToDate;
        exception.StartTime = request.StartTime;
        exception.EndTime = request.EndTime;
        exception.Reason = request.Reason.Trim();
        exception.DepartmentId = isExtra ? request.DepartmentId : null;
        exception.SlotMinutes = isExtra ? request.SlotMinutes : null;
        exception.CapacityPerSlot = isExtra ? request.CapacityPerSlot : null;
        exception.Mode = isExtra ? request.Mode ?? ConsultationMode.InPerson : null;
        exception.Room = isExtra ? Clean(request.Room) : null;

        await db.SaveChangesAsync(ct);
        IReadOnlyList<AffectedAppointment> affected = exception.RemovesSlots ? await BlockedBookingsAsync(exception, ct) : [];
        return new ScheduleSaved(exception.Id, affected);
    }

    public async Task<Result> DeleteExceptionAsync(Guid id, CancellationToken ct)
    {
        var exception = await db.Set<ScheduleException>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (exception is null)
        {
            return Error.NotFound(nameof(ScheduleException), id);
        }

        if (!BelongsToCurrentFacility(exception))
        {
            return OtherBranch;
        }

        db.Remove(exception);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<Result> CheckReferencesAsync(Guid practitionerId, Guid departmentId, CancellationToken ct)
    {
        if (!await db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId && p.IsActive, ct))
        {
            return Error.Field("practitionerId", "Choose an active doctor.");
        }

        return await db.Set<Department>().AnyAsync(d => d.Id == departmentId && d.IsActive, ct)
            ? Result.Success()
            : Error.Field("departmentId", "Choose an active department of this facility.");
    }

    /// <summary>Compares the candidate sessions with the doctor's other sessions at every branch.</summary>
    private async Task<Error?> FindOverlapAsync(IReadOnlyCollection<DoctorSchedule> candidates, CancellationToken ct)
    {
        var practitionerId = candidates.First().PractitionerId;
        var ids = candidates.Select(c => c.Id).ToList();
        var existing = await db.Set<DoctorSchedule>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => s.PractitionerId == practitionerId && s.IsActive && !ids.Contains(s.Id))
            .ToListAsync(ct);

        var clash = candidates.SelectMany(c => existing.Where(c.Overlaps).Select(e => (Candidate: c, Existing: e))).FirstOrDefault();
        return clash.Existing is null
            ? null
            : Error.Conflict(
                "schedule.overlap",
                $"The doctor already has a session on {clash.Existing.Weekday} {clash.Existing.StartTime:HH\\:mm}–{clash.Existing.EndTime:HH\\:mm}. Sessions cannot overlap.",
                new { scheduleId = clash.Existing.Id });
    }

    /// <summary>Future open bookings of this session that no longer fit its day, hours or validity.</summary>
    private async Task<IReadOnlyList<AffectedAppointment>> StrandedBookingsAsync(DoctorSchedule schedule, CancellationToken ct)
    {
        var bookings = await FutureOpenBookingsAsync(a => a.ScheduleId == schedule.Id, ct);
        return bookings
            .Where(b =>
            {
                var date = _calendar.DateOf(b.StartsAt);
                var start = _calendar.TimeOf(b.StartsAt);
                return !schedule.AppliesOn(date) || start < schedule.StartTime || _calendar.TimeOf(b.EndsAt) > schedule.EndTime;
            })
            .Select(b => b.Affected)
            .ToList();
    }

    private async Task<IReadOnlyList<AffectedAppointment>> BlockedBookingsAsync(ScheduleException exception, CancellationToken ct)
    {
        var from = _calendar.StartOfDay(exception.FromDate);
        var to = _calendar.StartOfDay(exception.ToDate.AddDays(1));
        var bookings = await FutureOpenBookingsAsync(a => a.PractitionerId == exception.PractitionerId && a.StartsAt >= from && a.StartsAt < to, ct);
        return bookings
            .Where(b => exception.Blocks(_calendar.DateOf(b.StartsAt), _calendar.TimeOf(b.StartsAt), _calendar.TimeOf(b.EndsAt), b.FacilityId))
            .Select(b => b.Affected)
            .ToList();
    }

    private async Task<List<BookingRow>> FutureOpenBookingsAsync(System.Linq.Expressions.Expression<Func<Appointment, bool>> filter, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await (
                from a in db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Where(filter)
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                where (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed) && a.StartsAt > now
                orderby a.StartsAt
                select new { a.Id, a.Number, a.PatientId, p.FullName, p.Phone, a.StartsAt, a.EndsAt, a.Status, a.FacilityId })
            .Take(500)
            .ToListAsync(ct);

        return rows.Select(r => new BookingRow(
            r.StartsAt, r.EndsAt, r.FacilityId,
            new AffectedAppointment(r.Id, r.Number, r.PatientId, r.FullName, Phone.Mask(r.Phone), r.StartsAt, r.Status))).ToList();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Error OtherBranch => Error.Forbidden("schedule.other_facility", "This entry belongs to another branch. Switch facility to change it.");

    /// <summary>Exceptions are not facility-filtered (leave spans branches), so branch-specific rows are checked explicitly.</summary>
    private bool BelongsToCurrentFacility(ScheduleException exception) =>
        exception.FacilityId is null || currentUser.FacilityId is null || exception.FacilityId == currentUser.FacilityId;

    private sealed record BookingRow(DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid FacilityId, AffectedAppointment Affected);
}
