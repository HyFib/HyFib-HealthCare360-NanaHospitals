using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

/// <summary>/api/v1/ot — theatre master, OT calendar (theatre and surgeon views), utilisation and implant recall trace.</summary>
internal static class TheatreEndpoints
{
    private const int MaxReportDays = 366;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ot").WithTags("Operation Theatre");

        group.MapGet("/theatres", ListTheatres).RequirePermission(SpecialtyCarePermissions.OtRead);
        group.MapPost("/theatres", (SaveTheatreRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveTheatre(null, r, db, u, ct))
            .RequirePermission(SpecialtyCarePermissions.OtTheatresManage).WithValidation<SaveTheatreRequest>();
        group.MapPut("/theatres/{id:guid}", (Guid id, SaveTheatreRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveTheatre(id, r, db, u, ct))
            .RequirePermission(SpecialtyCarePermissions.OtTheatresManage).WithValidation<SaveTheatreRequest>();

        group.MapGet("/calendar", Calendar).RequirePermission(SpecialtyCarePermissions.OtRead);
        group.MapGet("/utilisation", Utilisation).RequirePermission(SpecialtyCarePermissions.OtReports);
        group.MapGet("/implants/trace", TraceImplants).RequirePermission(SpecialtyCarePermissions.OtTheatresManage);
    }

    private static async Task<IResult> ListTheatres(bool? includeInactive, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<OperationTheatre>().AsNoTracking()
            .Where(t => includeInactive == true || t.IsActive)
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Code)
            .Select(t => new TheatreDto(t.Id, t.Code, t.Name, t.Type, t.Equipment, t.Location, t.DisplayOrder, t.IsActive, t.Version))
            .ToListAsync(ct));

    private static async Task<IResult> SaveTheatre(Guid? id, SaveTheatreRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<OperationTheatre>().AnyAsync(t => t.Code == code && t.Id != id, ct))
        {
            return Error.Conflict("ot.theatre_code_in_use", $"Theatre code {code} is already used.").ToProblem();
        }

        OperationTheatre theatre;
        if (id is { } existingId)
        {
            var found = await db.Set<OperationTheatre>().FirstOrDefaultAsync(t => t.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(OperationTheatre), existingId).ToProblem();
            }

            theatre = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(theatre, version);
            }
        }
        else
        {
            if (user.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.").ToProblem();
            }

            theatre = new OperationTheatre { FacilityId = facilityId };
            db.Add(theatre);
        }

        theatre.Code = code;
        theatre.Name = request.Name.Trim();
        theatre.Type = request.Type;
        theatre.Equipment = (request.Equipment ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        theatre.Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim();
        theatre.DisplayOrder = request.DisplayOrder;
        theatre.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/ot/theatres/{theatre.Id}", new { theatre.Id }) : TypedResults.NoContent();
    }

    /// <summary>Day (default) or week (Monday–Sunday) calendar; filter by theatre or show one surgeon's list.</summary>
    private static async Task<IResult> Calendar(DateOnly? date, string? view, Guid? theatreId, Guid? surgeonId, OtReportingService reports, TimeProvider time, CancellationToken ct)
    {
        var day = date ?? time.Today();
        var isWeek = string.Equals(view, "week", StringComparison.OrdinalIgnoreCase);
        if (view is not null && !isWeek && !string.Equals(view, "day", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Field("view", "Use 'day' or 'week'.").ToProblem();
        }

        var from = isWeek ? day.AddDays(-(((int)day.DayOfWeek + 6) % 7)) : day;
        var to = isWeek ? from.AddDays(6) : from;
        return TypedResults.Ok(await reports.CalendarAsync(from, to, theatreId, surgeonId, ct));
    }

    private static async Task<IResult> Utilisation(DateOnly? from, DateOnly? to, Guid? theatreId, int? minutesPerDay, OtReportingService reports, TimeProvider time, CancellationToken ct)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-29);
        if (start > end || end.DayNumber - start.DayNumber >= MaxReportDays)
        {
            return Error.Field("from", $"Choose a date range of 1 to {MaxReportDays} days.").ToProblem();
        }

        var sessionMinutes = minutesPerDay ?? OtReportingService.DefaultMinutesPerDay;
        if (sessionMinutes is < 60 or > 1440)
        {
            return Error.Field("minutesPerDay", "Planned OT minutes per day must be between 60 and 1440.").ToProblem();
        }

        return TypedResults.Ok(await reports.UtilisationAsync(start, end, theatreId, sessionMinutes, ct));
    }

    /// <summary>Device recall: every patient who received an implant with the given lot or serial number (audited).</summary>
    private static async Task<IResult> TraceImplants(string? lot, string? serial, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var lotNumber = string.IsNullOrWhiteSpace(lot) ? null : lot.Trim().ToUpperInvariant();
        var serialNumber = string.IsNullOrWhiteSpace(serial) ? null : serial.Trim().ToUpperInvariant();
        if (lotNumber is null && serialNumber is null)
        {
            return Error.Field("lot", "Enter a lot or serial number.").ToProblem();
        }

        var rows = await (
                from m in db.Set<SurgeryMaterial>().AsNoTracking()
                join b in db.Set<SurgeryBooking>() on m.SurgeryBookingId equals b.Id
                join p in db.Set<Patient>() on m.PatientId equals p.Id
                where m.Kind == MaterialKind.Implant
                      && (lotNumber == null || m.LotNumber == lotNumber)
                      && (serialNumber == null || m.SerialNumber == serialNumber)
                orderby m.CreatedAt descending
                select new { m.Id, m.SurgeryBookingId, b.BookingNumber, m.PatientId, p.Uhid, p.FullName, p.Phone, m.ItemName, m.Manufacturer, m.LotNumber, m.SerialNumber, m.CreatedAt })
            .Take(500)
            .ToListAsync(ct);

        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "ot.implant_trace", $"Implant trace for lot '{lotNumber}' serial '{serialNumber}': {rows.Count} patient record(s)")
        {
            EntityType = nameof(SurgeryMaterial),
            Severity = AuditSeverity.Warning,
        }, ct);

        return TypedResults.Ok(rows.Select(r => new ImplantTraceItem(
            r.Id, r.SurgeryBookingId, r.BookingNumber, r.PatientId, r.Uhid, r.FullName, Phone.Mask(r.Phone), r.ItemName, r.Manufacturer, r.LotNumber,
            r.SerialNumber, r.CreatedAt)).ToList());
    }
}
