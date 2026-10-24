using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Beds;

/// <summary>/api/v1/beds — bed masters, live bed board, occupancy, reservations, blocking, maintenance and housekeeping (WB-01/02/05).</summary>
internal static class BedEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/beds").WithTags("Beds");

        group.MapGet("/", List).RequireAnyPermission(MasterEndpoints.ReadPermissions);
        group.MapGet("/available", Available)
            .RequireAnyPermission(InpatientPermissions.Admit, InpatientPermissions.Transfer, InpatientPermissions.BedsManage);
        group.MapGet("/board", async (Guid? wardId, BedBoardService board, CancellationToken ct) => TypedResults.Ok(await board.BoardAsync(wardId, ct)))
            .RequirePermission(InpatientPermissions.AdmissionsRead);
        group.MapGet("/occupancy", async (BedBoardService board, CancellationToken ct) => TypedResults.Ok(await board.OccupancyAsync(ct)))
            .RequireAnyPermission(InpatientPermissions.AdmissionsRead, InpatientPermissions.BedsManage);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(MasterEndpoints.ReadPermissions);
        group.MapGet("/{id:guid}/history", History).RequirePermission(InpatientPermissions.AdmissionsRead);

        group.MapPost("/", Create).RequirePermission(InpatientPermissions.MastersManage).WithValidation<SaveBedRequest>();
        group.MapPost("/bulk", CreateMany).RequirePermission(InpatientPermissions.MastersManage).WithValidation<CreateBedsRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(InpatientPermissions.MastersManage).WithValidation<SaveBedRequest>().RequireVersion();

        group.MapPost("/{id:guid}/reserve", async (Guid id, ReserveBedRequest r, BedService beds, CancellationToken ct) => (await beds.ReserveAsync(id, r, ct)).ToOk())
            .RequirePermission(InpatientPermissions.BedsManage).WithValidation<ReserveBedRequest>();
        group.MapPost("/{id:guid}/cancel-reservation", async (Guid id, BedService beds, CancellationToken ct) => (await beds.CancelReservationAsync(id, ct)).ToOk())
            .RequirePermission(InpatientPermissions.BedsManage);
        group.MapPost("/{id:guid}/block", async (Guid id, BedReasonRequest r, BedService beds, CancellationToken ct) => (await beds.BlockAsync(id, r.Reason, ct)).ToOk())
            .RequirePermission(InpatientPermissions.BedsManage).WithValidation<BedReasonRequest>();
        group.MapPost("/{id:guid}/unblock", async (Guid id, BedService beds, CancellationToken ct) => (await beds.UnblockAsync(id, ct)).ToOk())
            .RequirePermission(InpatientPermissions.BedsManage);
        group.MapPost("/{id:guid}/maintenance", async (Guid id, BedReasonRequest r, BedService beds, CancellationToken ct) => (await beds.StartMaintenanceAsync(id, r.Reason, ct)).ToOk())
            .RequirePermission(InpatientPermissions.BedsManage).WithValidation<BedReasonRequest>();
        group.MapPost("/{id:guid}/maintenance/complete", async (Guid id, BedService beds, CancellationToken ct) => (await beds.EndMaintenanceAsync(id, ct)).ToOk())
            .RequirePermission(InpatientPermissions.BedsManage);
        group.MapPost("/{id:guid}/cleaning", async (Guid id, BedNoteRequest r, BedService beds, CancellationToken ct) => (await beds.MarkForCleaningAsync(id, r.Notes, ct)).ToOk())
            .RequireAnyPermission(InpatientPermissions.BedsManage, InpatientPermissions.BedsHousekeeping).WithValidation<BedNoteRequest>();

        group.MapGet("/housekeeping", async (HousekeepingStatus? status, Guid? wardId, BedBoardService board, CancellationToken ct) =>
                TypedResults.Ok(await board.HousekeepingAsync(status, wardId, ct)))
            .RequireAnyPermission(InpatientPermissions.BedsHousekeeping, InpatientPermissions.BedsManage);
        group.MapPost("/housekeeping/{taskId:guid}/start", async (Guid taskId, BedService beds, CancellationToken ct) => (await beds.StartCleaningAsync(taskId, ct)).ToOk())
            .RequirePermission(InpatientPermissions.BedsHousekeeping);
        group.MapPost("/housekeeping/{taskId:guid}/complete", async (Guid taskId, BedNoteRequest r, BedService beds, CancellationToken ct) =>
                (await beds.CompleteCleaningAsync(taskId, r.Notes, ct)).ToOk())
            .RequirePermission(InpatientPermissions.BedsHousekeeping).WithValidation<BedNoteRequest>();
    }

    private static async Task<IResult> List(Guid? wardId, BedStatus? status, bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var beds = db.Set<Bed>().AsNoTracking();
        if (includeInactive != true)
        {
            beds = beds.Where(b => b.IsActive);
        }

        if (wardId is not null)
        {
            beds = beds.Where(b => b.WardId == wardId);
        }

        if (status is not null)
        {
            beds = beds.Where(b => b.Status == status);
        }

        var rows = await (
                from b in beds
                join w in db.Set<Ward>() on b.WardId equals w.Id
                join r in db.Set<Room>() on b.RoomId equals r.Id into rooms
                from r in rooms.DefaultIfEmpty()
                orderby w.DisplayOrder, w.Name, b.DisplayOrder, b.Label
                select new { Bed = b, WardName = w.Name, RoomNumber = r != null ? r.Number : null })
            .Take(PageRequest.MaxPageSize * 5)
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(x => BedOperations.ToDto(x.Bed, x.WardName, x.RoomNumber)).ToList());
    }

    private static async Task<IResult> Available(Guid? patientId, WardType? wardType, Guid? wardId, BedBoardService board, CancellationToken ct) =>
        TypedResults.Ok(await board.AvailableAsync(patientId, wardType, wardId, ct));

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var bed = await db.Set<Bed>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return bed is null ? Error.NotFound(nameof(Bed), id).ToProblem() : TypedResults.Ok(await BedOperations.ToDtoAsync(db, bed, ct));
    }

    private static async Task<IResult> History(Guid id, [AsParameters] PageQuery query, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var result = await (
                from s in db.Set<BedAssignment>().AsNoTracking()
                join a in db.Set<Admission>() on s.AdmissionId equals a.Id
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                where s.BedId == id
                orderby s.FromAt descending
                select new BedHistoryItem(a.Id, a.AdmissionNumber, p.Id, p.Uhid, p.FullName, s.Reason, s.FromAt, s.ToAt))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Create(SaveBedRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var ward = await db.Set<Ward>().FirstOrDefaultAsync(w => w.Id == request.WardId, ct);
        if (ward is null)
        {
            return Error.Field("wardId", "Choose a ward.").ToProblem();
        }

        var problem = await CheckRoomAndLabelAsync(request.WardId, request.RoomId, request.Label.Trim(), null, db, ct);
        if (problem is not null)
        {
            return problem.ToProblem();
        }

        var bed = new Bed { FacilityId = ward.FacilityId, Status = BedStatus.Vacant };
        Apply(bed, request);
        db.Add(bed);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/beds/{bed.Id}", BedOperations.ToDto(bed, ward.Name, null));
    }

    private static async Task<IResult> CreateMany(CreateBedsRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var ward = await db.Set<Ward>().FirstOrDefaultAsync(w => w.Id == request.WardId, ct);
        if (ward is null)
        {
            return Error.Field("wardId", "Choose a ward.").ToProblem();
        }

        var prefix = request.LabelPrefix.Trim().ToUpperInvariant();
        var labels = Enumerable.Range(request.StartNumber, request.Count).Select(n => $"{prefix}-{n:00}").ToList();
        var taken = await db.Set<Bed>().Where(b => labels.Contains(b.Label)).Select(b => b.Label).ToListAsync(ct);
        if (taken.Count > 0)
        {
            return Error.Conflict("bed.label_in_use", $"These bed labels already exist: {string.Join(", ", taken)}.").ToProblem();
        }

        var order = await db.Set<Bed>().Where(b => b.WardId == ward.Id).MaxAsync(b => (int?)b.DisplayOrder, ct) ?? 0;
        var beds = labels.Select(label => new Bed
        {
            FacilityId = ward.FacilityId,
            WardId = ward.Id,
            RoomId = request.RoomId,
            Label = label,
            Status = BedStatus.Vacant,
            HasOxygen = request.HasOxygen,
            HasVentilator = request.HasVentilator,
            HasMonitor = request.HasMonitor,
            DisplayOrder = order += 10,
        }).ToList();
        db.AddRange(beds);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(beds.Select(b => BedOperations.ToDto(b, ward.Name, null)).ToList());
    }

    private static async Task<IResult> Update(Guid id, SaveBedRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var bed = await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bed is null)
        {
            return Error.NotFound(nameof(Bed), id).ToProblem();
        }

        var inUse = bed.Status is BedStatus.Occupied or BedStatus.Reserved;
        if (inUse && (request.WardId != bed.WardId || !request.IsActive))
        {
            return Error.Rule("bed.in_use", "An occupied or reserved bed cannot be moved to another ward or taken out of service.").ToProblem();
        }

        var problem = await CheckRoomAndLabelAsync(request.WardId, request.RoomId, request.Label.Trim(), id, db, ct);
        if (problem is not null)
        {
            return problem.ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(bed, version);
        }

        Apply(bed, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Error?> CheckRoomAndLabelAsync(Guid wardId, Guid? roomId, string label, Guid? excludeId, HospitalDbContext db, CancellationToken ct)
    {
        if (!await db.Set<Ward>().AnyAsync(w => w.Id == wardId, ct))
        {
            return Error.Field("wardId", "Choose a ward.");
        }

        if (roomId is { } room && !await db.Set<Room>().AnyAsync(r => r.Id == room && r.WardId == wardId, ct))
        {
            return Error.Field("roomId", "The room must belong to the selected ward.");
        }

        return await db.Set<Bed>().AnyAsync(b => b.Label == label && b.Id != excludeId, ct)
            ? Error.Conflict("bed.label_in_use", $"Bed label {label} is already in use.")
            : null;
    }

    private static void Apply(Bed bed, SaveBedRequest request)
    {
        bed.WardId = request.WardId;
        bed.RoomId = request.RoomId;
        bed.Label = request.Label.Trim();
        bed.HasOxygen = request.HasOxygen;
        bed.HasVentilator = request.HasVentilator;
        bed.HasMonitor = request.HasMonitor;
        bed.DisplayOrder = request.DisplayOrder;
        bed.IsActive = request.IsActive;
    }
}
