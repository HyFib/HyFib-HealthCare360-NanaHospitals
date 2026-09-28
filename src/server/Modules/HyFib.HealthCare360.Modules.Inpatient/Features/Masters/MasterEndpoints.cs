using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Masters;

/// <summary>/api/v1/inpatient/wards and /rooms — ward and room masters (WB-01). Bed masters live under /api/v1/beds.</summary>
internal static class MasterEndpoints
{
    public static readonly string[] ReadPermissions =
    [
        InpatientPermissions.AdmissionsRead, InpatientPermissions.BedsManage, InpatientPermissions.BedsHousekeeping,
        InpatientPermissions.NursingChart, InpatientPermissions.MastersManage,
    ];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inpatient").WithTags("In-patient masters");

        group.MapGet("/wards", ListWards).RequireAnyPermission(ReadPermissions);
        group.MapGet("/wards/{id:guid}", GetWard).RequireAnyPermission(ReadPermissions);
        group.MapPost("/wards", CreateWard).RequirePermission(InpatientPermissions.MastersManage).WithValidation<SaveWardRequest>();
        group.MapPut("/wards/{id:guid}", UpdateWard).RequirePermission(InpatientPermissions.MastersManage).WithValidation<SaveWardRequest>();

        group.MapGet("/wards/{wardId:guid}/rooms", ListRooms).RequireAnyPermission(ReadPermissions);
        group.MapPost("/rooms", CreateRoom).RequirePermission(InpatientPermissions.MastersManage).WithValidation<SaveRoomRequest>();
        group.MapPut("/rooms/{id:guid}", UpdateRoom).RequirePermission(InpatientPermissions.MastersManage).WithValidation<SaveRoomRequest>();
    }

    private static async Task<IResult> ListWards(bool? includeInactive, WardType? type, HospitalDbContext db, CancellationToken ct)
    {
        var wards = db.Set<Ward>().AsNoTracking();
        if (includeInactive != true)
        {
            wards = wards.Where(w => w.IsActive);
        }

        if (type is not null)
        {
            wards = wards.Where(w => w.Type == type);
        }

        return TypedResults.Ok(await Project(wards.OrderBy(w => w.DisplayOrder).ThenBy(w => w.Name), db).ToListAsync(ct));
    }

    private static async Task<IResult> GetWard(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var ward = await Project(db.Set<Ward>().AsNoTracking().Where(w => w.Id == id), db).FirstOrDefaultAsync(ct);
        return ward is null ? Error.NotFound(nameof(Ward), id).ToProblem() : TypedResults.Ok(ward);
    }

    private static async Task<IResult> CreateWard(SaveWardRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Ward>().AnyAsync(w => w.Code == code, ct))
        {
            return Error.Conflict("ward.code_in_use", $"Ward code {code} is already in use.").ToProblem();
        }

        var references = await CheckReferencesAsync(request, db, ct);
        if (references is not null)
        {
            return references.ToProblem();
        }

        var ward = new Ward { FacilityId = facilityId };
        Apply(ward, request, code);
        db.Add(ward);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/inpatient/wards/{ward.Id}", new { ward.Id });
    }

    private static async Task<IResult> UpdateWard(Guid id, SaveWardRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var ward = await db.Set<Ward>().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ward is null)
        {
            return Error.NotFound(nameof(Ward), id).ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Ward>().AnyAsync(w => w.Code == code && w.Id != id, ct))
        {
            return Error.Conflict("ward.code_in_use", $"Ward code {code} is already in use.").ToProblem();
        }

        if (!request.IsActive && ward.IsActive
            && await db.Set<Bed>().AnyAsync(b => b.WardId == id && (b.Status == BedStatus.Occupied || b.Status == BedStatus.Reserved), ct))
        {
            return Error.Rule("ward.in_use", "Move or discharge the patients (and release reservations) before closing this ward.").ToProblem();
        }

        var references = await CheckReferencesAsync(request, db, ct);
        if (references is not null)
        {
            return references.ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(ward, version);
        }

        Apply(ward, request, code);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListRooms(Guid wardId, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<Room>().AsNoTracking()
            .Where(r => r.WardId == wardId)
            .OrderBy(r => r.Number)
            .Select(r => new RoomDto(r.Id, r.WardId, r.Number, r.Description, r.IsIsolation, r.IsActive,
                db.Set<Bed>().Count(b => b.RoomId == r.Id && b.IsActive), r.Version))
            .ToListAsync(ct));

    private static async Task<IResult> CreateRoom(SaveRoomRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        if (!await db.Set<Ward>().AnyAsync(w => w.Id == request.WardId, ct))
        {
            return Error.Field("wardId", "Choose a ward.").ToProblem();
        }

        var number = request.Number.Trim();
        if (await db.Set<Room>().AnyAsync(r => r.WardId == request.WardId && r.Number == number, ct))
        {
            return Error.Conflict("room.number_in_use", $"Room {number} already exists in this ward.").ToProblem();
        }

        var room = new Room
        {
            FacilityId = facilityId,
            WardId = request.WardId,
            Number = number,
            Description = Clean(request.Description),
            IsIsolation = request.IsIsolation,
            IsActive = request.IsActive,
        };
        db.Add(room);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/inpatient/wards/{room.WardId}/rooms", new { room.Id });
    }

    private static async Task<IResult> UpdateRoom(Guid id, SaveRoomRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var room = await db.Set<Room>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (room is null)
        {
            return Error.NotFound(nameof(Room), id).ToProblem();
        }

        if (request.WardId != room.WardId && await db.Set<Bed>().AnyAsync(b => b.RoomId == id, ct))
        {
            return Error.Rule("room.has_beds", "Move the beds out of this room before moving it to another ward.").ToProblem();
        }

        var number = request.Number.Trim();
        if (await db.Set<Room>().AnyAsync(r => r.WardId == request.WardId && r.Number == number && r.Id != id, ct))
        {
            return Error.Conflict("room.number_in_use", $"Room {number} already exists in this ward.").ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(room, version);
        }

        room.WardId = request.WardId;
        room.Number = number;
        room.Description = Clean(request.Description);
        room.IsIsolation = request.IsIsolation;
        room.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static IQueryable<WardDto> Project(IQueryable<Ward> wards, HospitalDbContext db) =>
        wards.Select(w => new WardDto(
            w.Id, w.Code, w.Name, w.Type, w.Floor, w.Block, w.GenderPolicy, w.NurseStation, w.Phone, w.DepartmentId, w.BedServiceItemId,
            db.Set<ServiceItem>().Where(s => s.Id == w.BedServiceItemId).Select(s => s.Code).FirstOrDefault(),
            w.DisplayOrder, w.IsActive, w.Notes,
            db.Set<Bed>().Count(b => b.WardId == w.Id && b.IsActive),
            db.Set<Bed>().Count(b => b.WardId == w.Id && b.IsActive && b.Status == BedStatus.Occupied),
            w.Version));

    /// <summary>The bed tariff must be a bed-charge service (it is posted every day for every patient in the ward).</summary>
    private static async Task<Error?> CheckReferencesAsync(SaveWardRequest request, HospitalDbContext db, CancellationToken ct)
    {
        if (request.BedServiceItemId is { } serviceItemId
            && !await db.Set<ServiceItem>().AnyAsync(s => s.Id == serviceItemId && s.IsActive && s.Category == ServiceCategory.BedCharge, ct))
        {
            return Error.Field("bedServiceItemId", "Choose an active bed-charge service (BED-GEN, BED-ICU…).");
        }

        if (request.DepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId, ct))
        {
            return Error.Field("departmentId", "Choose a department.");
        }

        return null;
    }

    private static void Apply(Ward ward, SaveWardRequest request, string code)
    {
        ward.Code = code;
        ward.Name = request.Name.Trim();
        ward.Type = request.Type;
        ward.Floor = Clean(request.Floor);
        ward.Block = Clean(request.Block);
        ward.GenderPolicy = request.GenderPolicy;
        ward.NurseStation = Clean(request.NurseStation);
        ward.Phone = Phone.Normalize(request.Phone);
        ward.DepartmentId = request.DepartmentId;
        ward.BedServiceItemId = request.BedServiceItemId;
        ward.DisplayOrder = request.DisplayOrder;
        ward.IsActive = request.IsActive;
        ward.Notes = Clean(request.Notes);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
