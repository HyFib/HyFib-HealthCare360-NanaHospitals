using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Portal;

/// <summary>
/// /api/v1/portal/appointments and /api/v1/portal/waitlist — the patient's own and family appointments (OA-02..05,
/// PP-03). Every query is limited to <see cref="PortalPatientScope"/>; a record outside the scope answers 404 (not 403)
/// so the portal never confirms that someone else's appointment exists.
/// </summary>
internal static class PortalAppointmentEndpoints
{
    private const int MaxRows = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/portal/appointments").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal);
        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Book).WithValidation<PortalBookAppointmentRequest>();
        group.MapPost("/{id:guid}/cancel", Cancel).WithValidation<PortalCancelAppointmentRequest>();
        group.MapPost("/{id:guid}/reschedule", Reschedule).WithValidation<PortalRescheduleAppointmentRequest>();
        group.MapPost("/{id:guid}/payment", StartPayment);
        group.MapPost("/{id:guid}/payment/confirm", ConfirmPayment).WithValidation<ConfirmPaymentRequest>();

        var waitlist = app.MapGroup("/api/v1/portal/waitlist").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal);
        waitlist.MapGet("/", ListWaitlist);
        waitlist.MapPost("/", JoinWaitlist).WithValidation<PortalJoinWaitlistRequest>();
        waitlist.MapPost("/{id:guid}/cancel", LeaveWaitlist);
    }

    /// <param name="scope"><c>upcoming</c> (default), <c>past</c> or <c>all</c>.</param>
    private static async Task<IResult> List(
        string? scope,
        PortalPatientScope patients,
        HospitalDbContext db,
        SchedulingSettings settings,
        AppointmentPaymentService payments,
        TimeProvider time,
        CancellationToken ct)
    {
        var ids = await patients.AccessiblePatientIdsAsync(ct);
        if (ids.Count == 0)
        {
            return TypedResults.Ok(Array.Empty<PortalAppointment>());
        }

        var now = time.GetUtcNow();
        var recent = now.AddHours(-2);
        var appointments = db.Set<Appointment>().AsNoTracking().Where(a => ids.Contains(a.PatientId));
        var kind = scope?.Trim().ToLowerInvariant();
        appointments = kind switch
        {
            "past" => appointments.Where(a => a.StartsAt < recent
                                              || a.Status == AppointmentStatus.Completed || a.Status == AppointmentStatus.Cancelled
                                              || a.Status == AppointmentStatus.NoShow || a.Status == AppointmentStatus.Rescheduled),
            "all" => appointments,
            _ => appointments.Where(a => a.StartsAt >= recent
                                         && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed
                                             || a.Status == AppointmentStatus.CheckedIn || a.Status == AppointmentStatus.InConsultation)),
        };

        var newestFirst = kind is "past" or "all";
        var result = await MapAsync(db, appointments, newestFirst, settings, payments, now, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Get(
        Guid id,
        PortalPatientScope patients,
        HospitalDbContext db,
        SchedulingSettings settings,
        AppointmentPaymentService payments,
        TimeProvider time,
        CancellationToken ct)
    {
        var dto = await LoadOwnAsync(id, patients, db, settings, payments, time, ct);
        return dto is null ? Error.NotFound(nameof(Appointment), id).ToProblem() : TypedResults.Ok(dto);
    }

    /// <summary>Books for the signed-in patient or a family member; optional online prepayment (never loses the slot).</summary>
    private static async Task<IResult> Book(
        PortalBookAppointmentRequest request,
        PortalPatientScope patients,
        AppointmentBookingService booking,
        AppointmentPaymentService payments,
        HospitalDbContext db,
        SchedulingSettings settings,
        TimeProvider time,
        CancellationToken ct)
    {
        var access = await patients.EnsureAccessAsync(request.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var command = new BookingCommand(
            request.PatientId, request.PractitionerId, request.StartsAt, AppointmentChannel.Portal, request.VisitType, request.IsTelehealth, request.Reason, null)
        {
            Online = true,
        };
        var booked = await booking.BookAsync(command, ct);
        if (booked.IsFailure)
        {
            return booked.Error!.ToProblem();
        }

        var appointment = booked.Value;
        PaymentCheckout? checkout = null;
        string? message = null;
        if (request.PayOnline)
        {
            checkout = await payments.StartCheckoutAsync(appointment, ct);
            if (checkout is null)
            {
                message = "Online payment is not available right now. Your appointment is booked; please pay at the hospital.";
            }
        }
        else
        {
            appointment.ChoosePayAtHospital();
        }

        await db.SaveChangesAsync(ct);
        var dto = await LoadOwnAsync(appointment.Id, patients, db, settings, payments, time, ct);
        return dto is null
            ? Error.NotFound(nameof(Appointment), appointment.Id).ToProblem()
            : TypedResults.Created($"/api/v1/portal/appointments/{appointment.Id}", new PortalBookingResult(dto, checkout, message));
    }

    private static async Task<IResult> Cancel(
        Guid id,
        PortalCancelAppointmentRequest request,
        PortalPatientScope patients,
        AppointmentBookingService booking,
        HospitalDbContext db,
        SchedulingSettings settings,
        TimeProvider time,
        CancellationToken ct)
    {
        var appointment = await FindOwnAsync(id, patients, db, ct);
        if (appointment is null)
        {
            return Error.NotFound(nameof(Appointment), id).ToProblem();
        }

        var policy = await settings.OnlinePolicyAsync(appointment.FacilityId, ct);
        var allowed = BookingRules.EnsureOnlineChangeAllowed(appointment.StartsAt, time.GetUtcNow(), policy.Cutoff);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var result = await booking.CancelAsync(appointment, CancellationReason.PatientRequest, request.Reason, CancellationSource.Patient, notifyPatient: true, ct);
        return result.IsFailure ? result.Error!.ToProblem() : TypedResults.NoContent();
    }

    private static async Task<IResult> Reschedule(
        Guid id,
        PortalRescheduleAppointmentRequest request,
        PortalPatientScope patients,
        AppointmentBookingService booking,
        AppointmentPaymentService payments,
        HospitalDbContext db,
        SchedulingSettings settings,
        TimeProvider time,
        CancellationToken ct)
    {
        var appointment = await FindOwnAsync(id, patients, db, ct);
        if (appointment is null)
        {
            return Error.NotFound(nameof(Appointment), id).ToProblem();
        }

        var result = await booking.RescheduleAsync(appointment, new RescheduleCommand(request.StartsAt, null, null, request.Reason) { Online = true }, ct);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        var dto = await LoadOwnAsync(result.Value.Id, patients, db, settings, payments, time, ct);
        return dto is null ? Error.NotFound(nameof(Appointment), result.Value.Id).ToProblem() : TypedResults.Ok(dto);
    }

    /// <summary>Creates (or re-creates) a gateway order for an unpaid booking.</summary>
    private static async Task<IResult> StartPayment(
        Guid id,
        PortalPatientScope patients,
        AppointmentPaymentService payments,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var appointment = await FindOwnAsync(id, patients, db, ct);
        if (appointment is null)
        {
            return Error.NotFound(nameof(Appointment), id).ToProblem();
        }

        if (appointment.PaymentStatus == AppointmentPaymentStatus.PaidOnline)
        {
            return Error.Conflict("appointment.already_paid", "This appointment is already paid.").ToProblem();
        }

        if (!appointment.IsOpen)
        {
            return Error.Rule("appointment.not_open", "Only upcoming appointments can be paid online.").ToProblem();
        }

        var checkout = await payments.StartCheckoutAsync(appointment, ct);
        await db.SaveChangesAsync(ct);
        return checkout is null
            ? Error.Rule("payment.unavailable", "Online payment is not available right now. Please pay at the hospital.").ToProblem()
            : TypedResults.Ok(checkout);
    }

    /// <summary>Called by the portal after the gateway checkout succeeds; the signature is verified before anything is recorded.</summary>
    private static async Task<IResult> ConfirmPayment(
        Guid id,
        ConfirmPaymentRequest request,
        PortalPatientScope patients,
        AppointmentPaymentService payments,
        AppointmentBookingService booking,
        HospitalDbContext db,
        SchedulingSettings settings,
        TimeProvider time,
        CancellationToken ct)
    {
        var appointment = await FindOwnAsync(id, patients, db, ct);
        if (appointment is null)
        {
            return Error.NotFound(nameof(Appointment), id).ToProblem();
        }

        var result = await payments.ConfirmAsync(appointment, request.OrderId.Trim(), request.PaymentId.Trim(), request.Signature.Trim(), ct);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        await booking.PublishAsync(appointment, ct);
        var dto = await LoadOwnAsync(appointment.Id, patients, db, settings, payments, time, ct);
        return dto is null ? Error.NotFound(nameof(Appointment), id).ToProblem() : TypedResults.Ok(dto);
    }

    private static async Task<IResult> ListWaitlist(PortalPatientScope patients, HospitalDbContext db, CancellationToken ct)
    {
        var ids = await patients.AccessiblePatientIdsAsync(ct);
        return TypedResults.Ok(await ProjectWaitlist(db, db.Set<WaitlistEntry>().AsNoTracking().Where(w => ids.Contains(w.PatientId))).Take(MaxRows).ToListAsync(ct));
    }

    private static async Task<IResult> JoinWaitlist(
        PortalJoinWaitlistRequest request,
        PortalPatientScope patients,
        WaitlistService waitlist,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var access = await patients.EnsureAccessAsync(request.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var result = await waitlist.AddAsync(
            new WaitlistCommand(
                request.PatientId, request.PractitionerId, request.PreferredFrom, request.PreferredTo, request.PreferredSession,
                request.IsTelehealth, AppointmentChannel.Portal, request.Notes),
            ct);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        var entry = await ProjectWaitlist(db, db.Set<WaitlistEntry>().AsNoTracking().Where(w => w.Id == result.Value.Id)).FirstAsync(ct);
        return TypedResults.Created($"/api/v1/portal/waitlist/{entry.Id}", entry);
    }

    private static async Task<IResult> LeaveWaitlist(Guid id, PortalPatientScope patients, WaitlistService waitlist, HospitalDbContext db, CancellationToken ct)
    {
        var ids = await patients.AccessiblePatientIdsAsync(ct);
        var entry = await db.Set<WaitlistEntry>().FirstOrDefaultAsync(w => w.Id == id && ids.Contains(w.PatientId), ct);
        return entry is null ? Error.NotFound(nameof(WaitlistEntry), id).ToProblem() : (await waitlist.CancelAsync(entry, ct)).ToNoContent();
    }

    private static async Task<Appointment?> FindOwnAsync(Guid id, PortalPatientScope patients, HospitalDbContext db, CancellationToken ct)
    {
        var ids = await patients.AccessiblePatientIdsAsync(ct);
        return ids.Count == 0 ? null : await db.Set<Appointment>().FirstOrDefaultAsync(a => a.Id == id && ids.Contains(a.PatientId), ct);
    }

    private static async Task<PortalAppointment?> LoadOwnAsync(
        Guid id,
        PortalPatientScope patients,
        HospitalDbContext db,
        SchedulingSettings settings,
        AppointmentPaymentService payments,
        TimeProvider time,
        CancellationToken ct)
    {
        var ids = await patients.AccessiblePatientIdsAsync(ct);
        var rows = await MapAsync(
            db, db.Set<Appointment>().AsNoTracking().Where(a => a.Id == id && ids.Contains(a.PatientId)), false, settings, payments, time.GetUtcNow(), ct);
        return rows.FirstOrDefault();
    }

    private static async Task<List<PortalAppointment>> MapAsync(
        HospitalDbContext db,
        IQueryable<Appointment> appointments,
        bool newestFirst,
        SchedulingSettings settings,
        AppointmentPaymentService payments,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var limited = (newestFirst ? appointments.OrderByDescending(a => a.StartsAt) : appointments.OrderBy(a => a.StartsAt)).Take(MaxRows);
        var rows = await (
                from a in limited
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                join pr in db.Set<Practitioner>() on a.PractitionerId equals pr.Id into practitioners
                from pr in practitioners.DefaultIfEmpty()
                join d in db.Set<Department>() on a.DepartmentId equals d.Id into departments
                from d in departments.DefaultIfEmpty()
                join f in db.Set<Facility>() on a.FacilityId equals f.Id into facilities
                from f in facilities.DefaultIfEmpty()
                select new
                {
                    Appointment = a,
                    PatientName = p.FullName,
                    PractitionerName = pr != null ? pr.DisplayName : null,
                    SpecialtyId = pr != null ? pr.SpecialtyId : null,
                    DepartmentName = d != null ? d.Name : null,
                    FacilityName = f != null ? f.Name : null,
                })
            .ToListAsync(ct);

        var specialtyIds = rows.Where(r => r.SpecialtyId != null).Select(r => r.SpecialtyId!.Value).Distinct().ToList();
        var specialties = await db.Set<Specialty>().AsNoTracking().Where(s => specialtyIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var policy = await settings.OnlinePolicyAsync(null, ct);

        var ordered = newestFirst ? rows.OrderByDescending(r => r.Appointment.StartsAt) : rows.OrderBy(r => r.Appointment.StartsAt);
        return ordered.Select(r =>
        {
            var a = r.Appointment;
            var changeable = a.IsOpen && a.StartsAt - now >= policy.Cutoff;
            return new PortalAppointment(
                a.Id, a.Number, a.PatientId, r.PatientName, a.PractitionerId, r.PractitionerName ?? "Doctor",
                r.SpecialtyId is { } s ? specialties.GetValueOrDefault(s) : null, r.DepartmentName, a.FacilityId, r.FacilityName,
                a.StartsAt, a.EndsAt, a.Status, a.VisitType, a.IsTelehealth, a.PaymentStatus, a.FeeAmount, AppointmentPaymentService.Currency, a.Reason,
                CanCancel: changeable,
                CanReschedule: changeable,
                CanPay: payments.IsAvailable && a.IsOpen && a.PaymentStatus != AppointmentPaymentStatus.PaidOnline && a.FeeAmount > 0 && a.StartsAt > now);
        }).ToList();
    }

    private static IQueryable<PortalWaitlistEntry> ProjectWaitlist(HospitalDbContext db, IQueryable<WaitlistEntry> entries) =>
        from w in entries
        join p in db.Set<Patient>() on w.PatientId equals p.Id
        join pr in db.Set<Practitioner>() on w.PractitionerId equals pr.Id into practitioners
        from pr in practitioners.DefaultIfEmpty()
        orderby w.CreatedAt descending
        select new PortalWaitlistEntry(
            w.Id, w.PatientId, p.FullName, w.PractitionerId, pr != null ? pr.DisplayName : "Doctor", w.PreferredFrom, w.PreferredTo,
            w.PreferredSession, w.IsTelehealth, w.Status, w.OfferedSlotStartsAt, w.CreatedAt);
}
