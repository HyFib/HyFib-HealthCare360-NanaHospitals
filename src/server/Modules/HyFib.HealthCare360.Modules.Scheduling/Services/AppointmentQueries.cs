using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>Filters for staff appointment lists and calendars (dates are local IST calendar dates).</summary>
public sealed record AppointmentFilter(
    DateOnly From,
    DateOnly To,
    Guid? PractitionerId,
    Guid? DepartmentId,
    Guid? PatientId,
    AppointmentStatus? Status,
    AppointmentChannel? Channel,
    bool? Telehealth);

/// <summary>Read models for appointments (projections joined with patient, doctor and department names).</summary>
public static class AppointmentQueries
{
    private const int MaxHistory = 10;

    public static IQueryable<Appointment> Apply(this IQueryable<Appointment> appointments, AppointmentFilter filter)
    {
        var calendar = HospitalCalendar.India;
        var start = calendar.StartOfDay(filter.From);
        var end = calendar.StartOfDay(filter.To.AddDays(1));
        appointments = appointments.Where(a => a.StartsAt >= start && a.StartsAt < end);

        if (filter.PractitionerId is { } practitionerId)
        {
            appointments = appointments.Where(a => a.PractitionerId == practitionerId);
        }

        if (filter.DepartmentId is { } departmentId)
        {
            appointments = appointments.Where(a => a.DepartmentId == departmentId);
        }

        if (filter.PatientId is { } patientId)
        {
            appointments = appointments.Where(a => a.PatientId == patientId);
        }

        if (filter.Status is { } status)
        {
            appointments = appointments.Where(a => a.Status == status);
        }

        if (filter.Channel is { } channel)
        {
            appointments = appointments.Where(a => a.Channel == channel);
        }

        if (filter.Telehealth is { } telehealth)
        {
            appointments = appointments.Where(a => a.IsTelehealth == telehealth);
        }

        return appointments;
    }

    public static async Task<AppointmentDetail?> DetailAsync(HospitalDbContext db, Guid id, DateOnly today, CancellationToken ct)
    {
        var row = await (
                from a in db.Set<Appointment>().AsNoTracking()
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                join pr in db.Set<Practitioner>() on a.PractitionerId equals pr.Id into practitioners
                from pr in practitioners.DefaultIfEmpty()
                join d in db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on a.DepartmentId equals d.Id into departments
                from d in departments.DefaultIfEmpty()
                where a.Id == id
                select new
                {
                    Appointment = a,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    p.Phone,
                    PractitionerName = pr != null ? pr.DisplayName : null,
                    DepartmentName = d != null ? d.Name : null,
                })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var x = row.Appointment;
        var history = await HistoryAsync(db, x, ct);
        return new AppointmentDetail(
            x.Id, x.Number, x.FacilityId, x.PatientId, row.Uhid, row.FullName, row.Gender, row.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            row.Phone, x.PractitionerId, row.PractitionerName ?? "Doctor", x.DepartmentId, row.DepartmentName, x.ScheduleId, x.StartsAt, x.EndsAt,
            x.SeatNumber, x.Status, x.Channel, x.VisitType, x.IsTelehealth, x.Room, x.Reason, x.Notes, x.BookedAt, x.ConfirmedAt, x.FeeAmount,
            x.PaymentStatus, x.PaymentProvider, x.GatewayOrderId, x.GatewayPaymentId, x.AmountPaid, x.PaidAt, x.VisitId, x.CheckedInAt,
            x.CompletedAt, x.NoShowAt, x.CancelReasonCode, x.CancelReason, x.CancelledBy, x.CancelledAt, x.RescheduledFromId, x.RescheduledToId,
            x.WaitlistEntryId, x.Reminder24hSentAt, x.Reminder2hSentAt, history, x.Version);
    }

    /// <summary>Earlier bookings this appointment replaced (reschedule chain), newest first.</summary>
    private static async Task<IReadOnlyList<AppointmentHistoryItem>> HistoryAsync(HospitalDbContext db, Appointment appointment, CancellationToken ct)
    {
        var history = new List<AppointmentHistoryItem>();
        var previousId = appointment.RescheduledFromId;
        while (previousId is { } id && history.Count < MaxHistory)
        {
            var previous = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(a => a.Id == id)
                .Select(a => new { a.Id, a.Number, a.PractitionerId, a.StartsAt, a.Status, a.BookedAt, a.CancelReason, a.RescheduledFromId })
                .FirstOrDefaultAsync(ct);
            if (previous is null)
            {
                break;
            }

            history.Add(new AppointmentHistoryItem(previous.Id, previous.Number, previous.PractitionerId, previous.StartsAt, previous.Status, previous.BookedAt, previous.CancelReason));
            previousId = previous.RescheduledFromId;
        }

        return history;
    }

    /// <summary>Sort keys the list accepts (anything else falls back to appointment time).</summary>
    private static readonly IReadOnlyDictionary<string, Expression<Func<AppointmentRow, object?>>> SortKeys =
        new Dictionary<string, Expression<Func<AppointmentRow, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["startsAt"] = r => r.StartsAt,
            ["number"] = r => r.Number,
            ["patient"] = r => r.PatientName,
            ["status"] = r => r.Status,
            ["bookedAt"] = r => r.BookedAt,
        };

    public static async Task<PagedResult<AppointmentListItem>> ListAsync(
        HospitalDbContext db, IQueryable<Appointment> appointments, PageRequest page, DateOnly today, CancellationToken ct)
    {
        var rows = from a in appointments
                   join p in db.Set<Patient>() on a.PatientId equals p.Id
                   join pr in db.Set<Practitioner>() on a.PractitionerId equals pr.Id into practitioners
                   from pr in practitioners.DefaultIfEmpty()
                   join d in db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on a.DepartmentId equals d.Id into departments
                   from d in departments.DefaultIfEmpty()
                   select new AppointmentRow
                   {
                       Id = a.Id,
                       Number = a.Number,
                       PatientId = a.PatientId,
                       Uhid = p.Uhid,
                       PatientName = p.FullName,
                       Gender = p.Gender,
                       DateOfBirth = p.DateOfBirth,
                       Phone = p.Phone,
                       PractitionerId = a.PractitionerId,
                       PractitionerName = pr != null ? pr.DisplayName : null,
                       DepartmentId = a.DepartmentId,
                       DepartmentName = d != null ? d.Name : null,
                       StartsAt = a.StartsAt,
                       EndsAt = a.EndsAt,
                       SeatNumber = a.SeatNumber,
                       Status = a.Status,
                       Channel = a.Channel,
                       VisitType = a.VisitType,
                       IsTelehealth = a.IsTelehealth,
                       PaymentStatus = a.PaymentStatus,
                       FeeAmount = a.FeeAmount,
                       Reason = a.Reason,
                       Room = a.Room,
                       VisitId = a.VisitId,
                       BookedAt = a.BookedAt,
                   };

        var result = await rows.OrderByKey(page, SortKeys, r => r.StartsAt).ToPagedResultAsync(page, ct);
        return result.Map(r => new AppointmentListItem(
            r.Id, r.Number, r.PatientId, r.Uhid, r.PatientName, r.Gender, r.DateOfBirth is { } dob ? Age.Format(dob, today) : null, Phone.Mask(r.Phone),
            r.PractitionerId, r.PractitionerName ?? "Doctor", r.DepartmentId, r.DepartmentName, r.StartsAt, r.EndsAt, r.SeatNumber, r.Status,
            r.Channel, r.VisitType, r.IsTelehealth, r.PaymentStatus, r.FeeAmount, r.Reason, r.Room, r.VisitId, r.BookedAt));
    }

    public static Task<List<CalendarAppointment>> CalendarAsync(HospitalDbContext db, IQueryable<Appointment> appointments, int limit, CancellationToken ct) =>
        (from a in appointments
         join p in db.Set<Patient>() on a.PatientId equals p.Id
         orderby a.StartsAt, a.SeatNumber
         select new CalendarAppointment(
             a.Id, a.Number, a.PatientId, p.FullName, p.Uhid, a.PractitionerId, a.DepartmentId, a.StartsAt, a.EndsAt, a.Status, a.VisitType,
             a.Channel, a.IsTelehealth, a.PaymentStatus))
        .Take(limit)
        .ToListAsync(ct);

    /// <summary>Flat projection used for sorting and paging the staff list.</summary>
    internal sealed class AppointmentRow
    {
        public Guid Id { get; init; }
        public string Number { get; init; } = string.Empty;
        public Guid PatientId { get; init; }
        public string Uhid { get; init; } = string.Empty;
        public string PatientName { get; init; } = string.Empty;
        public Gender Gender { get; init; }
        public DateOnly? DateOfBirth { get; init; }
        public string? Phone { get; init; }
        public Guid PractitionerId { get; init; }
        public string? PractitionerName { get; init; }
        public Guid DepartmentId { get; init; }
        public string? DepartmentName { get; init; }
        public DateTimeOffset StartsAt { get; init; }
        public DateTimeOffset EndsAt { get; init; }
        public int SeatNumber { get; init; }
        public AppointmentStatus Status { get; init; }
        public AppointmentChannel Channel { get; init; }
        public AppointmentVisitType VisitType { get; init; }
        public bool IsTelehealth { get; init; }
        public AppointmentPaymentStatus PaymentStatus { get; init; }
        public decimal FeeAmount { get; init; }
        public string? Reason { get; init; }
        public string? Room { get; init; }
        public Guid? VisitId { get; init; }
        public DateTimeOffset BookedAt { get; init; }
    }
}
