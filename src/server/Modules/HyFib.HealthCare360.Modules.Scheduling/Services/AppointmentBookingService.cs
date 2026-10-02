using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;
using Npgsql;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>One booking request, whichever channel it came from (desk, phone, portal, waitlist, reschedule).</summary>
public sealed record BookingCommand(
    Guid PatientId,
    Guid PractitionerId,
    DateTimeOffset StartsAt,
    AppointmentChannel Channel,
    AppointmentVisitType VisitType,
    bool IsTelehealth,
    string? Reason,
    string? Notes)
{
    /// <summary>Staff may knowingly put a patient into a full slot (needs <c>scheduling.appointments.manage</c>).</summary>
    public bool Overbook { get; init; }

    /// <summary>Apply online rules: only online-bookable doctors, cut-off, booking window, one booking per doctor per day.</summary>
    public bool Online { get; init; }

    public bool NotifyPatient { get; init; } = true;

    public Guid? WaitlistEntryId { get; init; }
}

public sealed record RescheduleCommand(DateTimeOffset StartsAt, Guid? PractitionerId, bool? IsTelehealth, string? Reason)
{
    public bool Overbook { get; init; }

    public bool Online { get; init; }

    public bool NotifyPatient { get; init; } = true;
}

public sealed record CheckInOptions(PayerType PayerType, Guid? PayerId, TokenPriority Priority, string? ChiefComplaint, bool IssueToken);

public sealed record CheckInOutcome(Guid AppointmentId, Guid VisitId, string VisitNumber, string? TokenLabel, bool LinkedExistingVisit);

/// <summary>
/// Appointment lifecycle (PMS-02/03, OA-02..05). Booking runs in one transaction that first takes PostgreSQL advisory
/// locks on the slot and on the patient, then re-computes the slot, checks capacity and the patient's other bookings,
/// allocates a seat and a number, and queues the confirmation. A partial unique index on
/// (practitioner, start, seat) is the final guarantee that capacity is never exceeded, even across API nodes.
/// </summary>
public sealed class AppointmentBookingService(
    HospitalDbContext db,
    AvailabilityService availability,
    INumberSeries numbers,
    AppointmentNotifier notifier,
    WaitlistService waitlist,
    SchedulingSettings settings,
    VisitService visits,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string ChangedEvent = "appointments.changed";

    public Task<Result<Appointment>> BookAsync(BookingCommand command, CancellationToken ct) => PlaceAsync(command, null, null, ct);

    public async Task<Result<Appointment>> RescheduleAsync(Guid appointmentId, RescheduleCommand command, CancellationToken ct)
    {
        var current = await db.Set<Appointment>().FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
        return current is null ? Error.NotFound(nameof(Appointment), appointmentId) : await RescheduleAsync(current, command, ct);
    }

    /// <summary>Books the new slot and marks <paramref name="current"/> as Rescheduled in the same transaction (history is kept).</summary>
    public async Task<Result<Appointment>> RescheduleAsync(Appointment current, RescheduleCommand command, CancellationToken ct)
    {
        if (!current.IsOpen)
        {
            return Error.Rule("appointment.not_open", "Only booked or confirmed appointments can be rescheduled.");
        }

        var practitionerId = command.PractitionerId ?? current.PractitionerId;
        if (practitionerId == current.PractitionerId && command.StartsAt == current.StartsAt)
        {
            return Error.Rule("appointment.same_slot", "Choose a different time to reschedule.");
        }

        if (command.Online)
        {
            var policy = await settings.OnlinePolicyAsync(current.FacilityId, ct);
            var change = BookingRules.EnsureOnlineChangeAllowed(current.StartsAt, time.GetUtcNow(), policy.Cutoff);
            if (change.IsFailure)
            {
                return change.Error!;
            }
        }

        var booking = new BookingCommand(
            current.PatientId, practitionerId, command.StartsAt, current.Channel, current.VisitType,
            command.IsTelehealth ?? current.IsTelehealth, current.Reason, current.Notes)
        {
            Overbook = command.Overbook,
            Online = command.Online,
            NotifyPatient = command.NotifyPatient,
        };
        return await PlaceAsync(booking, current, command.Reason, ct);
    }

    public async Task<Result<Appointment>> CancelAsync(Guid appointmentId, CancellationReason code, string? reason, CancellationSource by, bool notifyPatient, CancellationToken ct)
    {
        var appointment = await db.Set<Appointment>().FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
        return appointment is null
            ? Error.NotFound(nameof(Appointment), appointmentId)
            : await CancelAsync(appointment, code, reason, by, notifyPatient, ct);
    }

    /// <summary>Cancels, tells the patient, and offers the freed slot to the waitlist — all in one unit of work.</summary>
    public async Task<Result<Appointment>> CancelAsync(Appointment appointment, CancellationReason code, string? reason, CancellationSource by, bool notifyPatient, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var result = appointment.Cancel(code, reason, by, currentUser.UserId, now);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        if (notifyPatient)
        {
            await notifier.CancelledAsync(appointment, ct);
        }

        await waitlist.OfferFreedSlotAsync(appointment, now, ct);
        await db.SaveChangesAsync(ct);
        await PublishAsync(appointment, ct);
        return appointment;
    }

    public Task<Result<Appointment>> ConfirmAsync(Guid appointmentId, CancellationToken ct) =>
        ChangeAsync(appointmentId, (appointment, now) => appointment.Confirm(now), ct);

    public Task<Result<Appointment>> MarkNoShowAsync(Guid appointmentId, CancellationToken ct) =>
        ChangeAsync(appointmentId, (appointment, now) => appointment.MarkNoShow(now), ct);

    /// <summary>
    /// Front-desk check-in (PMS-03): creates the visit and queue token through the Patients module. The appointment is
    /// marked CheckedIn by the <c>VisitCreated</c> handler inside the visit's own transaction. When the patient was
    /// already registered as a walk-in today, the appointment is linked to that open visit instead of failing.
    /// </summary>
    public async Task<Result<CheckInOutcome>> CheckInAsync(Guid appointmentId, CheckInOptions options, CancellationToken ct)
    {
        var appointment = await db.Set<Appointment>().FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
        if (appointment is null)
        {
            return Error.NotFound(nameof(Appointment), appointmentId);
        }

        var allowed = appointment.EnsureCanCheckIn(time.Today(), availability.Calendar);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var created = await visits.CreateAsync(new CreateVisitRequest(
            appointment.PatientId,
            appointment.IsTelehealth ? VisitType.Telehealth : VisitType.OutPatient,
            appointment.DepartmentId,
            appointment.PractitionerId,
            appointment.Id,
            options.PayerType,
            options.PayerId,
            appointment.Channel is AppointmentChannel.Online or AppointmentChannel.Portal ? ReferralSource.Online : ReferralSource.Self,
            null,
            string.IsNullOrWhiteSpace(options.ChiefComplaint) ? appointment.Reason : options.ChiefComplaint.Trim(),
            false,
            null,
            null,
            options.Priority,
            IssueToken: options.IssueToken && !appointment.IsTelehealth,
            NotifyPatient: true), ct);

        if (created.IsSuccess)
        {
            await PublishAsync(appointment, ct);
            return new CheckInOutcome(appointment.Id, created.Value.Id, created.Value.VisitNumber, created.Value.Token?.Label, false);
        }

        var error = created.Error!;
        if (error.Code != "visit.already_open")
        {
            return error;
        }

        var dayStart = availability.Calendar.StartOfDay(time.Today());
        var open = await db.Set<Visit>().AsNoTracking()
            .Where(v => v.PatientId == appointment.PatientId && v.DepartmentId == appointment.DepartmentId && v.Type == VisitType.OutPatient
                        && (v.Status == VisitStatus.Open || v.Status == VisitStatus.InProgress) && v.VisitedAt >= dayStart)
            .OrderByDescending(v => v.VisitedAt)
            .Select(v => new { v.Id, v.VisitNumber })
            .FirstOrDefaultAsync(ct);
        if (open is null)
        {
            return error;
        }

        var linked = appointment.CheckIn(open.Id, time.GetUtcNow());
        if (linked.IsFailure)
        {
            return linked.Error!;
        }

        var token = await db.Set<QueueToken>().AsNoTracking().Where(t => t.VisitId == open.Id).Select(t => t.Label).FirstOrDefaultAsync(ct);
        await db.SaveChangesAsync(ct);
        await PublishAsync(appointment, ct);
        return new CheckInOutcome(appointment.Id, open.Id, open.VisitNumber, token, true);
    }

    /// <summary>Real-time refresh for calendars and the front-office board (ids and status only, never PHI).</summary>
    internal Task PublishAsync(Appointment appointment, CancellationToken ct) =>
        realtime.PublishAsync(
            Topics.Facility(appointment.FacilityId),
            ChangedEvent,
            new { appointment.Id, appointment.PractitionerId, appointment.DepartmentId, appointment.Status, appointment.StartsAt },
            ct);

    private async Task<Result<Appointment>> ChangeAsync(Guid appointmentId, Func<Appointment, DateTimeOffset, Result> change, CancellationToken ct)
    {
        var appointment = await db.Set<Appointment>().FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
        if (appointment is null)
        {
            return Error.NotFound(nameof(Appointment), appointmentId);
        }

        var result = change(appointment, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(appointment, ct);
        return appointment;
    }

    private async Task<Result<Appointment>> PlaceAsync(BookingCommand command, Appointment? replacing, string? rescheduleReason, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (command.StartsAt <= now)
        {
            return Error.Rule("appointment.past", "This time has already passed. Choose a later slot.");
        }

        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), command.PatientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another UHID. Book with the surviving record.");
        }

        if (patient.IsDeceased || !patient.IsActive)
        {
            return Error.Rule("patient.inactive", "Appointments cannot be booked for inactive or deceased patients.");
        }

        var practitioner = await db.Set<Practitioner>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.PractitionerId, ct);
        if (practitioner is null || !practitioner.IsActive)
        {
            return Error.Field("practitionerId", "Choose an active doctor.");
        }

        if (command.IsTelehealth && !practitioner.AvailableForTelehealth)
        {
            return Error.Rule("appointment.telehealth_unavailable", $"{practitioner.DisplayName} does not offer video consultations.");
        }

        if (command.Online)
        {
            if (!practitioner.AvailableForOnlineBooking)
            {
                return Error.Rule("appointment.online_unavailable", $"{practitioner.DisplayName} is not available for online booking. Please call the hospital.");
            }

            var policy = await settings.OnlinePolicyAsync(null, ct);
            var online = BookingRules.EnsureOnlineBookable(command.StartsAt, now, policy);
            if (online.IsFailure)
            {
                return online.Error!;
            }
        }

        var calendar = availability.Calendar;
        var date = calendar.DateOf(command.StartsAt);
        var replacingId = replacing?.Id ?? Guid.Empty;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AdvisoryLocks.AcquireAsync(db, [AdvisoryLocks.SlotKey(command.PractitionerId, command.StartsAt), AdvisoryLocks.PatientKey(command.PatientId)], ct);

        var mode = command.IsTelehealth ? ConsultationMode.Telehealth : ConsultationMode.InPerson;
        var slots = await availability.ComputeAsync(command.PractitionerId, new SlotQuery(date, date, now) { Mode = mode, IncludeFull = true }, ct);
        var slot = slots.FirstOrDefault(s => s.StartsAt == command.StartsAt);
        var slotCheck = BookingRules.EnsureSlotCanTake(slot, command.Overbook);
        if (slotCheck.IsFailure || slot is null)
        {
            return slotCheck.Error ?? Error.Rule("appointment.slot_unavailable", "This time is not available for booking.");
        }

        var patientBookings = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(Appointment.HoldsSeatExpression)
            .Where(a => a.PatientId == patient.Id && a.StartsAt < slot.EndsAt && a.EndsAt > slot.StartsAt && a.Id != replacingId)
            .Select(a => new PatientBooking(a.Id, a.Number, a.StartsAt, a.EndsAt))
            .ToListAsync(ct);
        var overlap = BookingRules.EnsureNoOverlap(patientBookings, slot.StartsAt, slot.EndsAt);
        if (overlap.IsFailure)
        {
            return overlap.Error!;
        }

        if (command.Online)
        {
            var dayStart = calendar.StartOfDay(date);
            var dayEnd = calendar.StartOfDay(date.AddDays(1));
            var sameDay = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .AnyAsync(a => a.PatientId == patient.Id && a.PractitionerId == practitioner.Id && a.Id != replacingId
                               && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed)
                               && a.StartsAt >= dayStart && a.StartsAt < dayEnd, ct);
            if (sameDay)
            {
                return Error.Conflict("appointment.duplicate_day", $"{patient.FirstName} already has an appointment with {practitioner.DisplayName} on this day.");
            }
        }

        var takenSeats = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(Appointment.HoldsSeatExpression)
            .Where(a => a.PractitionerId == practitioner.Id && a.StartsAt == slot.StartsAt)
            .Select(a => a.SeatNumber)
            .ToListAsync(ct);
        var seat = BookingRules.NextSeat(takenSeats, slot.Capacity, command.Overbook);
        if (seat is null)
        {
            return Error.Conflict("appointment.slot_full", "This slot has just been booked. Choose another slot.");
        }

        var number = await numbers.NextAsync(SchedulingNumberSeries.Appointment, slot.FacilityId, ct);
        var appointment = Appointment.Book(
            new AppointmentBooking(
                slot.FacilityId, patient.Id, practitioner.Id, slot.DepartmentId, slot.ScheduleId, slot.StartsAt, slot.EndsAt, seat.Value,
                command.Channel, command.VisitType, command.IsTelehealth, slot.Room,
                BookingRules.FeeFor(command.VisitType, practitioner.ConsultationFeeNew, practitioner.ConsultationFeeFollowUp),
                Clean(command.Reason), Clean(command.Notes), currentUser.UserId),
            number,
            now);
        db.Add(appointment);

        if (replacing is not null)
        {
            var moved = replacing.MarkRescheduled(appointment, rescheduleReason, currentUser.UserId, now);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }

            await waitlist.OfferFreedSlotAsync(replacing, now, ct);
        }

        await waitlist.CloseForBookingAsync(appointment, command.WaitlistEntryId, now, ct);
        if (command.NotifyPatient)
        {
            await notifier.ConfirmedAsync(appointment, ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another node took the same seat despite the lock (for example a manual database edit): report "full".
            db.ChangeTracker.Clear();
            return Error.Conflict("appointment.slot_full", "This slot has just been booked. Choose another slot.");
        }

        await PublishAsync(appointment, ct);
        if (replacing is not null)
        {
            await PublishAsync(replacing, ct);
        }

        return appointment;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
