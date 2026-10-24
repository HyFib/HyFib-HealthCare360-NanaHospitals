using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;

/// <summary>/api/v1/scheduling/appointments — staff booking, calendar, reschedule, cancel, no-show and check-in (PMS-02/03).</summary>
internal static class AppointmentEndpoints
{
    private const int MaxCalendarDays = 31;
    private const int MaxListDays = 3 * 366;
    private const int MaxCalendarRows = 3000;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/scheduling/appointments").WithTags("Appointments");

        group.MapGet("/", List).RequirePermission(SchedulingPermissions.AppointmentsRead);
        group.MapGet("/calendar", Calendar).RequirePermission(SchedulingPermissions.AppointmentsRead);
        group.MapGet("/summary", Summary).RequirePermission(SchedulingPermissions.AppointmentsRead);
        group.MapGet("/{id:guid}", Get).RequirePermission(SchedulingPermissions.AppointmentsRead);
        group.MapPost("/", Book).RequirePermission(SchedulingPermissions.AppointmentsBook).WithValidation<BookAppointmentRequest>();
        group.MapPost("/{id:guid}/reschedule", Reschedule).RequirePermission(SchedulingPermissions.AppointmentsManage).WithValidation<RescheduleAppointmentRequest>();
        group.MapPost("/{id:guid}/cancel", Cancel).RequirePermission(SchedulingPermissions.AppointmentsManage).WithValidation<CancelAppointmentRequest>();
        group.MapPost("/{id:guid}/confirm", Confirm).RequirePermission(SchedulingPermissions.AppointmentsManage);
        group.MapPost("/{id:guid}/no-show", NoShow).RequirePermission(SchedulingPermissions.AppointmentsManage);
        group.MapPost("/{id:guid}/check-in", CheckIn).RequirePermission(SchedulingPermissions.AppointmentsManage).WithValidation<CheckInAppointmentRequest>();
    }

    /// <summary>Paged list for the appointment desk; a patient's history when <paramref name="patientId"/> is given.</summary>
    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        Guid? practitionerId,
        Guid? departmentId,
        Guid? patientId,
        AppointmentStatus? status,
        AppointmentChannel? channel,
        bool? telehealth,
        HospitalDbContext db,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.Today();
        var start = from ?? (patientId is null ? today : today.AddYears(-2));
        var end = to ?? (patientId is null ? start : today.AddYears(1));
        if (end < start || end.DayNumber - start.DayNumber > MaxListDays)
        {
            return Error.Field("to", "Choose a valid date range (at most three years).").ToProblem();
        }

        var appointments = db.Set<Appointment>().AsNoTracking()
            .Apply(new AppointmentFilter(start, end, practitionerId, departmentId, patientId, status, channel, telehealth));

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            appointments = appointments.Where(a => a.Number == number || matching.Contains(a.PatientId));
        }

        return TypedResults.Ok(await AppointmentQueries.ListAsync(db, appointments, page, today, ct));
    }

    /// <summary>Compact rows for day/week calendars. Cancelled and rescheduled bookings are hidden unless asked for.</summary>
    private static async Task<IResult> Calendar(
        DateOnly? from,
        DateOnly? to,
        Guid? practitionerId,
        Guid? departmentId,
        AppointmentStatus? status,
        bool? includeCancelled,
        HospitalDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var start = from ?? time.Today();
        var end = to ?? start.AddDays(6);
        if (end < start || end.DayNumber - start.DayNumber >= MaxCalendarDays)
        {
            return Error.Field("to", $"A calendar can show 1 to {MaxCalendarDays} days.").ToProblem();
        }

        var appointments = db.Set<Appointment>().AsNoTracking()
            .Apply(new AppointmentFilter(start, end, practitionerId, departmentId, null, status, null, null));
        if (includeCancelled != true && status is null)
        {
            appointments = appointments.Where(Appointment.HoldsSeatExpression);
        }

        return TypedResults.Ok(await AppointmentQueries.CalendarAsync(db, appointments, MaxCalendarRows, ct));
    }

    /// <summary>Front-office day board: status counts and each doctor's load against capacity for one day.</summary>
    private static async Task<IResult> Summary(
        DateOnly? date,
        Guid? departmentId,
        HospitalDbContext db,
        AvailabilityService availability,
        TimeProvider time,
        CancellationToken ct)
    {
        var day = date ?? time.Today();
        var appointments = await db.Set<Appointment>().AsNoTracking()
            .Apply(new AppointmentFilter(day, day, null, departmentId, null, null, null, null))
            .Select(a => new { a.PractitionerId, a.Status, a.IsTelehealth, a.Channel })
            .ToListAsync(ct);

        var scheduled = await db.Set<DoctorSchedule>().AsNoTracking()
            .Where(s => s.IsActive && (departmentId == null || s.DepartmentId == departmentId))
            .Select(s => s.PractitionerId)
            .Distinct()
            .ToListAsync(ct);
        var ids = scheduled.Union(appointments.Select(a => a.PractitionerId)).ToList();

        var slots = await availability.ComputeAsync(ids, new SlotQuery(day, day, DateTimeOffset.MinValue) { IncludeFull = true }, ct);
        var doctors = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.DisplayName, p.DepartmentId })
            .ToDictionaryAsync(p => p.Id, ct);

        var loads = ids
            .Select(id =>
            {
                var mine = appointments.Where(a => a.PractitionerId == id).ToList();
                var daySlots = (slots.GetValueOrDefault(id) ?? []).Where(s => departmentId is null || s.DepartmentId == departmentId).ToList();
                var doctor = doctors.GetValueOrDefault(id);
                return new PractitionerDayLoad(
                    id,
                    doctor?.DisplayName ?? "Doctor",
                    doctor?.DepartmentId,
                    daySlots.Sum(s => s.Capacity),
                    mine.Count(a => a.Status is not (AppointmentStatus.Cancelled or AppointmentStatus.Rescheduled)),
                    mine.Count(a => a.Status is AppointmentStatus.CheckedIn or AppointmentStatus.InConsultation),
                    mine.Count(a => a.Status == AppointmentStatus.Completed),
                    mine.Count(a => a.Status == AppointmentStatus.NoShow),
                    mine.Count(a => a.Status == AppointmentStatus.Cancelled),
                    daySlots.Count(s => !s.IsFull));
            })
            .Where(l => l.Capacity > 0 || l.Booked > 0 || l.Cancelled > 0)
            .OrderBy(l => l.PractitionerName)
            .ToList();

        return TypedResults.Ok(new AppointmentDaySummary(
            day,
            appointments.Count,
            appointments.Count(a => a.Status == AppointmentStatus.Booked),
            appointments.Count(a => a.Status == AppointmentStatus.Confirmed),
            appointments.Count(a => a.Status == AppointmentStatus.CheckedIn),
            appointments.Count(a => a.Status == AppointmentStatus.InConsultation),
            appointments.Count(a => a.Status == AppointmentStatus.Completed),
            appointments.Count(a => a.Status == AppointmentStatus.Cancelled),
            appointments.Count(a => a.Status == AppointmentStatus.NoShow),
            appointments.Count(a => a.IsTelehealth && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.Rescheduled),
            appointments.Count(a => a.Channel is AppointmentChannel.Online or AppointmentChannel.Portal),
            loads));
    }

    private static async Task<IResult> Get(
        Guid id, HospitalDbContext db, PatientAccessGuard guard, PatientContactAccess contacts, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var detail = await AppointmentQueries.DetailAsync(db, id, time.Today(), await contacts.FullAsync(ct), ct);
        if (detail is null)
        {
            return Error.NotFound(nameof(Appointment), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(detail.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(detail.PatientId, $"Viewed appointment {detail.Number}", ct);
        return TypedResults.Ok(detail);
    }

    private static async Task<IResult> Book(
        BookAppointmentRequest request,
        AppointmentBookingService booking,
        IPermissionService permissions,
        ICurrentUser user,
        HospitalDbContext db,
        PatientContactAccess contacts,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request.Overbook && !await CanManageAsync(user, permissions, ct))
        {
            return Error.Forbidden("appointment.overbook_denied", "Only staff who manage appointments can overbook a full slot.").ToProblem();
        }

        var command = new BookingCommand(
            request.PatientId, request.PractitionerId, request.StartsAt, request.Channel, request.VisitType, request.IsTelehealth, request.Reason, request.Notes)
        {
            Overbook = request.Overbook,
            NotifyPatient = request.NotifyPatient,
            WaitlistEntryId = request.WaitlistEntryId,
        };
        return await ToDetailResultAsync(await booking.BookAsync(command, ct), db, contacts, time, created: true, ct);
    }

    private static async Task<IResult> Reschedule(
        Guid id,
        RescheduleAppointmentRequest request,
        AppointmentBookingService booking,
        HospitalDbContext db,
        PatientContactAccess contacts,
        TimeProvider time,
        CancellationToken ct)
    {
        var command = new RescheduleCommand(request.StartsAt, request.PractitionerId, request.IsTelehealth, request.Reason)
        {
            Overbook = request.Overbook,
            NotifyPatient = request.NotifyPatient,
        };
        return await ToDetailResultAsync(await booking.RescheduleAsync(id, command, ct), db, contacts, time, created: false, ct);
    }

    private static async Task<IResult> Cancel(
        Guid id,
        CancelAppointmentRequest request,
        AppointmentBookingService booking,
        HospitalDbContext db,
        PatientContactAccess contacts,
        TimeProvider time,
        CancellationToken ct) =>
        await ToDetailResultAsync(
            await booking.CancelAsync(id, request.ReasonCode, request.Reason, CancellationSource.Staff, request.NotifyPatient, ct), db, contacts, time,
            created: false, ct);

    private static async Task<IResult> Confirm(
        Guid id, AppointmentBookingService booking, HospitalDbContext db, PatientContactAccess contacts, TimeProvider time, CancellationToken ct) =>
        await ToDetailResultAsync(await booking.ConfirmAsync(id, ct), db, contacts, time, created: false, ct);

    private static async Task<IResult> NoShow(
        Guid id, AppointmentBookingService booking, HospitalDbContext db, PatientContactAccess contacts, TimeProvider time, CancellationToken ct) =>
        await ToDetailResultAsync(await booking.MarkNoShowAsync(id, ct), db, contacts, time, created: false, ct);

    private static async Task<IResult> CheckIn(Guid id, CheckInAppointmentRequest request, AppointmentBookingService booking, CancellationToken ct)
    {
        var result = await booking.CheckInAsync(id, new CheckInOptions(request.PayerType, request.PayerId, request.Priority, request.ChiefComplaint, request.IssueToken), ct);
        return result.IsFailure
            ? result.Error!.ToProblem()
            : TypedResults.Ok(new CheckInResult(result.Value.AppointmentId, result.Value.VisitId, result.Value.VisitNumber, result.Value.TokenLabel, result.Value.LinkedExistingVisit));
    }

    private static async Task<bool> CanManageAsync(ICurrentUser user, IPermissionService permissions, CancellationToken ct) =>
        user.UserId is { } userId && await permissions.HasPermissionAsync(userId, SchedulingPermissions.AppointmentsManage, ct);

    internal static async Task<IResult> ToDetailResultAsync(
        Result<Appointment> result, HospitalDbContext db, PatientContactAccess contacts, TimeProvider time, bool created, CancellationToken ct)
    {
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        var detail = await AppointmentQueries.DetailAsync(db, result.Value.Id, time.Today(), await contacts.FullAsync(ct), ct);
        if (detail is null)
        {
            return Error.NotFound(nameof(Appointment), result.Value.Id).ToProblem();
        }

        return created ? TypedResults.Created($"/api/v1/scheduling/appointments/{detail.Id}", detail) : TypedResults.Ok(detail);
    }
}
