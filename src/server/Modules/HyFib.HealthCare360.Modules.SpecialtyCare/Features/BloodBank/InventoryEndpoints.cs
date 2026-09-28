using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;

/// <summary>/api/v1/blood-bank/inventory and /units — stock dashboard, unit look-up by barcode and discards.</summary>
internal static class InventoryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/blood-bank").WithTags("Blood Bank");

        group.MapGet("/inventory", async (BloodInventoryService inventory, CancellationToken ct) => TypedResults.Ok(await inventory.DashboardAsync(ct)))
            .RequirePermission(SpecialtyCarePermissions.BloodBankRead);
        group.MapGet("/units", ListUnits).RequirePermission(SpecialtyCarePermissions.BloodBankRead);
        group.MapGet("/units/by-number/{unitNumber}", GetByNumber).RequirePermission(SpecialtyCarePermissions.BloodBankRead);
        group.MapPost("/units/{id:guid}/discard", Discard).RequirePermission(SpecialtyCarePermissions.BloodBankInventory).WithValidation<DiscardUnitRequest>();
    }

    private static async Task<IResult> ListUnits(
        [AsParameters] PageQuery query, BloodUnitStatus? status, BloodComponentType? component, BloodGroup? bloodGroup, HospitalDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var units = db.Set<BloodComponent>().AsNoTracking();
        if (status is not null)
        {
            units = units.Where(u => u.Status == status);
        }

        if (component is not null)
        {
            units = units.Where(u => u.ComponentType == component);
        }

        if (bloodGroup is not null)
        {
            units = units.Where(u => u.BloodGroup == bloodGroup);
        }

        if (page.SearchTerm is { } term)
        {
            var prefix = term.ToUpperInvariant();
            units = units.Where(u => u.UnitNumber.StartsWith(prefix));
        }

        var result = await units.OrderBy(u => u.ExpiresAt).ToPagedResultAsync(page, ct);
        var now = time.GetUtcNow();
        return TypedResults.Ok(result.Map(u => ToDto(u, now)));
    }

    /// <summary>Barcode scan at the counter: the bag number printed on the unit label.</summary>
    private static async Task<IResult> GetByNumber(string unitNumber, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var number = unitNumber.Trim().ToUpperInvariant();
        var unit = await db.Set<BloodComponent>().AsNoTracking().FirstOrDefaultAsync(u => u.UnitNumber == number, ct);
        return unit is null ? Error.NotFound("Blood unit", number).ToProblem() : TypedResults.Ok(ToDto(unit, time.GetUtcNow()));
    }

    private static async Task<IResult> Discard(Guid id, DiscardUnitRequest request, HospitalDbContext db, IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var unit = await db.Set<BloodComponent>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (unit is null)
        {
            return Error.NotFound("Blood unit", id).ToProblem();
        }

        var wasReservedFor = unit.Status == BloodUnitStatus.Reserved ? unit.ReservedForRequestId : null;
        var result = unit.Discard(request.Reason, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        if (wasReservedFor is { } requestId && await db.Set<BloodRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct) is { } bloodRequest)
        {
            bloodRequest.OnReservationReleased();
        }

        await db.SaveChangesAsync(ct);
        await realtime.FacilityChangedAsync(unit.FacilityId, SpecialtyRealtime.BloodBankChanged, new { area = "inventory" }, ct);
        return TypedResults.NoContent();
    }

    internal static BloodUnitDto ToDto(BloodComponent u, DateTimeOffset now) => new(
        u.Id, u.UnitNumber, u.DonationId, u.ComponentType, u.BloodGroup, u.VolumeMl, u.CollectedAt, u.ExpiresAt, u.Status, u.Tti, u.StorageLocation,
        u.ReservedForRequestId, u.IssuedAt, u.DiscardReason, u.Status == BloodUnitStatus.Expired || u.IsExpiredAt(now));
}
