using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Waitlist;

/// <summary>/api/v1/scheduling/waitlist — patients waiting for a slot with a doctor (OA-05).</summary>
internal static class WaitlistEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/scheduling/waitlist").WithTags("Appointments");

        group.MapGet("/", List).RequirePermission(SchedulingPermissions.AppointmentsRead);
        group.MapPost("/", Add).RequirePermission(SchedulingPermissions.AppointmentsBook).WithValidation<AddToWaitlistRequest>();
        group.MapPost("/{id:guid}/cancel", Cancel).RequirePermission(SchedulingPermissions.AppointmentsManage);
        group.MapPost("/{id:guid}/book", Book).RequirePermission(SchedulingPermissions.AppointmentsBook).WithValidation<BookFromWaitlistRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        Guid? practitionerId,
        Guid? departmentId,
        WaitlistStatus? status,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var entries = db.Set<WaitlistEntry>().AsNoTracking();
        entries = status is { } wanted
            ? entries.Where(w => w.Status == wanted)
            : entries.Where(w => w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered);

        if (practitionerId is not null)
        {
            entries = entries.Where(w => w.PractitionerId == practitionerId);
        }

        if (departmentId is not null)
        {
            entries = entries.Where(w => w.DepartmentId == departmentId);
        }

        var result = await Project(db, entries).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(w => w with { PhoneMasked = Phone.Mask(w.PhoneMasked) }));
    }

    private static async Task<IResult> Add(AddToWaitlistRequest request, WaitlistService waitlist, HospitalDbContext db, CancellationToken ct)
    {
        var result = await waitlist.AddAsync(
            new WaitlistCommand(
                request.PatientId, request.PractitionerId, request.PreferredFrom, request.PreferredTo, request.PreferredSession,
                request.IsTelehealth, request.Channel, request.Notes),
            ct);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        var dto = await Project(db, db.Set<WaitlistEntry>().AsNoTracking().Where(w => w.Id == result.Value.Id)).FirstAsync(ct);
        return TypedResults.Created($"/api/v1/scheduling/waitlist/{dto.Id}", dto with { PhoneMasked = Phone.Mask(dto.PhoneMasked) });
    }

    private static async Task<IResult> Cancel(Guid id, WaitlistService waitlist, HospitalDbContext db, CancellationToken ct)
    {
        var entry = await db.Set<WaitlistEntry>().FirstOrDefaultAsync(w => w.Id == id, ct);
        return entry is null ? Error.NotFound(nameof(WaitlistEntry), id).ToProblem() : (await waitlist.CancelAsync(entry, ct)).ToNoContent();
    }

    private static async Task<IResult> Book(
        Guid id,
        BookFromWaitlistRequest request,
        AppointmentBookingService booking,
        IPermissionService permissions,
        ICurrentUser user,
        HospitalDbContext db,
        PatientContactAccess contacts,
        TimeProvider time,
        CancellationToken ct)
    {
        var entry = await db.Set<WaitlistEntry>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (entry is null)
        {
            return Error.NotFound(nameof(WaitlistEntry), id).ToProblem();
        }

        if (!entry.IsOpen)
        {
            return Error.Rule("waitlist.closed", "This waitlist request is closed.").ToProblem();
        }

        if (request.Overbook && !(user.UserId is { } userId && await permissions.HasPermissionAsync(userId, SchedulingPermissions.AppointmentsManage, ct)))
        {
            return Error.Forbidden("appointment.overbook_denied", "Only staff who manage appointments can overbook a full slot.").ToProblem();
        }

        var command = new BookingCommand(
            entry.PatientId, entry.PractitionerId, request.StartsAt, entry.Channel, request.VisitType, entry.IsTelehealth,
            request.Reason, entry.Notes)
        {
            Overbook = request.Overbook,
            WaitlistEntryId = entry.Id,
        };
        return await AppointmentEndpoints.ToDetailResultAsync(await booking.BookAsync(command, ct), db, contacts, time, created: true, ct);
    }

    /// <summary>
    /// Joins patient and doctor names, earliest preferred date first. The raw phone is carried in PhoneMasked and
    /// masked after materialisation (masking cannot run in SQL).
    /// </summary>
    private static IQueryable<WaitlistEntryDto> Project(HospitalDbContext db, IQueryable<WaitlistEntry> entries) =>
        from w in entries
        join p in db.Set<Patient>() on w.PatientId equals p.Id
        join pr in db.Set<Practitioner>() on w.PractitionerId equals pr.Id into practitioners
        from pr in practitioners.DefaultIfEmpty()
        orderby w.PreferredFrom, w.CreatedAt
        select new WaitlistEntryDto(
            w.Id, w.PatientId, p.Uhid, p.FullName, p.Phone, w.PractitionerId, pr != null ? pr.DisplayName : "Doctor", w.DepartmentId,
            w.PreferredFrom, w.PreferredTo, w.PreferredSession, w.IsTelehealth, w.Channel, w.Notes, w.Status, w.CreatedAt, w.OfferedAt,
            w.OfferedSlotStartsAt, w.OfferCount, w.AppointmentId);
}
