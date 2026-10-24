using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

/// <summary>/api/v1/ot/bookings — surgery requests, scheduling and the surgery lifecycle.</summary>
internal static class SurgeryEndpoints
{
    private static readonly Dictionary<string, Expression<Func<SurgeryBooking, object?>>> SortKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scheduledStart"] = b => b.ScheduledStart,
        ["bookingNumber"] = b => b.BookingNumber,
        ["status"] = SqlOrdering.RankKey<SurgeryBooking, SurgeryStatus>(b => b.Status),
        // Enums are stored as text; rank priority explicitly so ascending order means emergency first.
        ["priority"] = b => b.Priority == SurgeryPriority.Emergency ? 0 : b.Priority == SurgeryPriority.Urgent ? 1 : 2,
        ["createdAt"] = b => b.CreatedAt,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ot/bookings").WithTags("Operation Theatre");

        group.MapGet("/", List).RequirePermission(SpecialtyCarePermissions.OtRead);
        group.MapGet("/{id:guid}", Get).RequirePermission(SpecialtyCarePermissions.OtRead);
        group.MapPost("/", async (CreateSurgeryBookingRequest r, SurgeryService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToCreated(c => $"/api/v1/ot/bookings/{c.Id}"))
            .RequirePermission(SpecialtyCarePermissions.OtBookingsManage).WithValidation<CreateSurgeryBookingRequest>();
        group.MapPut("/{id:guid}", async (Guid id, UpdateSurgeryPlanRequest r, SurgeryService s, CancellationToken ct) =>
                (await s.UpdatePlanAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.OtBookingsManage).WithValidation<UpdateSurgeryPlanRequest>();
        group.MapPost("/{id:guid}/schedule", async (Guid id, ScheduleSurgeryRequest r, SurgeryService s, CancellationToken ct) =>
                (await s.ScheduleAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.OtBookingsManage).WithValidation<ScheduleSurgeryRequest>();
        group.MapPost("/{id:guid}/postpone", async (Guid id, SurgeryReasonRequest r, SurgeryService s, CancellationToken ct) =>
                (await s.PostponeAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.OtBookingsManage).WithValidation<SurgeryReasonRequest>();
        group.MapPost("/{id:guid}/cancel", async (Guid id, SurgeryReasonRequest r, SurgeryService s, CancellationToken ct) =>
                (await s.CancelAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.OtBookingsManage).WithValidation<SurgeryReasonRequest>();
        group.MapPost("/{id:guid}/receive", async (Guid id, SurgeryService s, CancellationToken ct) => (await s.ReceiveInPreOpAsync(id, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.OtChecklists);
        group.MapPost("/{id:guid}/start", async (Guid id, SurgeryService s, CancellationToken ct) => (await s.StartAsync(id, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.OtSurgery);
        group.MapPost("/{id:guid}/complete", async (Guid id, SurgeryService s, CancellationToken ct) => (await s.CompleteAsync(id, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.OtSurgery);
        group.MapPost("/{id:guid}/charges/retry", RetryCharges).RequirePermission(SpecialtyCarePermissions.OtBookingsManage);

        app.MapGet("/api/v1/patients/{patientId:guid}/surgeries", ForPatient)
            .WithTags("Operation Theatre")
            .RequirePermission(SpecialtyCarePermissions.OtRead);
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        SurgeryStatus? status,
        Guid? theatreId,
        Guid? surgeonId,
        HospitalDbContext db,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var bookings = db.Set<SurgeryBooking>().AsNoTracking();
        if (from is not null || to is not null)
        {
            var first = from ?? to!.Value;
            var last = to ?? from!.Value;
            var (start, end) = SpecialtyClock.RangeUtc(first, last);
            bookings = bookings.Where(b => (b.ScheduledStart >= start && b.ScheduledStart < end)
                                           || (b.ScheduledStart == null && b.PreferredDate >= first && b.PreferredDate <= last));
        }

        if (status is not null)
        {
            bookings = bookings.Where(b => b.Status == status);
        }

        if (theatreId is not null)
        {
            bookings = bookings.Where(b => b.TheatreId == theatreId);
        }

        if (surgeonId is not null)
        {
            bookings = bookings.Where(b => b.SurgeonId == surgeonId);
        }

        if (page.SearchTerm is { } term)
        {
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            var number = term.ToUpperInvariant();
            bookings = bookings.Where(b => matching.Contains(b.PatientId) || b.BookingNumber == number);
        }

        var result = await (
                from b in bookings.OrderByKey(page, SortKeys, b => b.CreatedAt, defaultDescending: true)
                join p in db.Set<Patient>() on b.PatientId equals p.Id
                join t in db.Set<OperationTheatre>() on b.TheatreId equals t.Id into theatres
                from t in theatres.DefaultIfEmpty()
                select new
                {
                    b.Id,
                    b.BookingNumber,
                    b.PatientId,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    b.ProcedureName,
                    b.Laterality,
                    b.Priority,
                    b.Status,
                    b.TheatreId,
                    TheatreCode = t != null ? t.Code : null,
                    b.ScheduledStart,
                    b.ScheduledEnd,
                    b.PreferredDate,
                    b.SurgeonId,
                    b.AnaesthesiaType,
                    SignIn = b.SignInCompletedAt != null,
                    TimeOut = b.TimeOutCompletedAt != null,
                    SignOut = b.SignOutCompletedAt != null,
                    b.ChargeStatus,
                })
            .ToPagedResultAsync(page, ct);

        var today = time.Today();
        var names = await SpecialtyLookups.PractitionerNamesAsync(db, result.Items.Select(i => i.SurgeonId), ct);
        return TypedResults.Ok(result.Map(r => new SurgeryListItem(
            r.Id, r.BookingNumber, r.PatientId, r.Uhid, r.FullName, r.Gender, r.DateOfBirth is { } dob ? Age.Format(dob, today) : null, r.ProcedureName,
            r.Laterality, r.Priority, r.Status, r.TheatreId, r.TheatreCode, r.ScheduledStart, r.ScheduledEnd, r.PreferredDate, r.SurgeonId,
            r.SurgeonId is { } s ? names.GetValueOrDefault(s) : null, r.AnaesthesiaType, r.SignIn, r.TimeOut, r.SignOut, r.ChargeStatus)));
    }

    private static async Task<IResult> Get(
        Guid id, HospitalDbContext db, PatientAccessGuard guard, PatientContactAccess contacts, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(booking.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(booking.PatientId, $"Viewed surgery {booking.BookingNumber}", ct);
        var patients = await PatientQueries.SummariesAsync(db, [booking.PatientId], time.Today(), await contacts.FullAsync(ct), ct);
        var names = await SpecialtyLookups.PractitionerNamesAsync(db, [booking.SurgeonId, booking.AnaesthetistId], ct);
        var theatre = booking.TheatreId is { } theatreId
            ? await db.Set<OperationTheatre>().AsNoTracking().Where(t => t.Id == theatreId).Select(t => t.Name).FirstOrDefaultAsync(ct)
            : null;
        return TypedResults.Ok(ToDetail(booking, patients.GetValueOrDefault(booking.PatientId), names, theatre));
    }

    private static async Task<IResult> ForPatient(
        Guid patientId, [AsParameters] PageQuery query, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, "Viewed surgery history", ct);
        var page = query.ToRequest();
        var result = await db.Set<SurgeryBooking>().AsNoTracking()
            .Where(b => b.PatientId == patientId)
            .OrderByDescending(b => b.ScheduledStart ?? b.CreatedAt)
            .Select(b => new { b.Id, b.BookingNumber, b.ProcedureName, b.Laterality, b.Status, b.ScheduledStart, b.CompletedAt, b.SurgeonId })
            .ToPagedResultAsync(page, ct);
        var names = await SpecialtyLookups.PractitionerNamesAsync(db, result.Items.Select(i => i.SurgeonId), ct);
        return TypedResults.Ok(result.Map(b => new
        {
            b.Id,
            b.BookingNumber,
            b.ProcedureName,
            b.Laterality,
            b.Status,
            b.ScheduledStart,
            b.CompletedAt,
            SurgeonName = b.SurgeonId is { } s ? names.GetValueOrDefault(s) : null,
        }));
    }

    private static async Task<IResult> RetryCharges(Guid id, SurgeryService surgeries, OtClinicalService clinical, CancellationToken ct)
    {
        var booking = await surgeries.RetryChargesAsync(id, ct);
        if (booking.IsFailure)
        {
            return booking.Error!.ToProblem();
        }

        var materials = await clinical.RetryMaterialChargesAsync(id, ct);
        var failed = new[] { booking.Value, materials }.FirstOrDefault(o => o.Status == ChargeStatus.Failed);
        return TypedResults.Ok(failed ?? ChargeOutcome.Posted);
    }

    internal static SurgeryDetail ToDetail(SurgeryBooking b, PatientSummary? patient, IReadOnlyDictionary<Guid, string> names, string? theatreName) => new(
        b.Id, b.BookingNumber, patient, b.PatientId, b.AdmissionId, b.VisitId, b.OrderItemId, b.ProcedureServiceItemId, b.ProcedureCode, b.ProcedureName,
        b.Laterality, b.Diagnosis, b.SurgeonId, b.SurgeonId is { } s ? names.GetValueOrDefault(s) : null, b.AssistantIds, b.AnaesthetistId,
        b.AnaesthetistId is { } a ? names.GetValueOrDefault(a) : null, b.AnaesthesiaType, b.Priority, b.PreferredDate, b.TheatreId, theatreName,
        b.ScheduledStart, b.ScheduledEnd, b.Status, b.ReceivedInPreOpAt, b.InRoomAt, b.StartedAt, b.CompletedAt,
        PreOpChecklist.IsComplete(b.PreOpChecks),
        PreOpChecklist.Items.Select(i => ToItem(i, b.PreOpChecks.FirstOrDefault(e => e.Key == i.Key))).ToList(),
        SurgicalSafetyChecklist.Phases.Select(p => new SafetyPhaseDto(
            p.Key,
            b.IsPhaseComplete(p.Key),
            p.Key switch { SafetyPhase.SignIn => b.SignInCompletedAt, SafetyPhase.TimeOut => b.TimeOutCompletedAt, _ => b.SignOutCompletedAt },
            p.Value.Select(i => ToItem(i, b.SafetyChecks.FirstOrDefault(e => e.Phase == p.Key && e.Key == i.Key))).ToList())).ToList(),
        b.Notes, b.CancelReason, b.PostponeReason, b.PostponeCount, b.ChargeStatus, b.ChargeError, b.Version);

    private static ChecklistItemDto ToItem(ChecklistItemDefinition definition, ChecklistEntry? entry) => new(
        definition.Key, definition.Text, definition.AllowsNotApplicable, entry?.State ?? ChecklistItemState.Pending, entry?.Remarks,
        entry?.RecordedByName, entry?.RecordedAt);

    private static ChecklistItemDto ToItem(ChecklistItemDefinition definition, SafetyChecklistEntry? entry) => new(
        definition.Key, definition.Text, definition.AllowsNotApplicable, entry?.State ?? ChecklistItemState.Pending, entry?.Remarks,
        entry?.ConfirmedByName, entry?.ConfirmedAt);
}
