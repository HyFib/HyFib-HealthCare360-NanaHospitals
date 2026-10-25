using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Linen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Linen;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Linen and laundry: the items and their expected life, the count at each place (store, wards against their par
/// levels, the laundry), and every movement between them counted in pieces. Infected soiled linen is kept apart and
/// washed only by thermal disinfection.
/// </summary>
internal sealed class LinenService(HospitalDbContext db, INumberSeries numbers, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<IReadOnlyList<LinenItemDto>> ItemsAsync(CancellationToken ct)
    {
        var items = await db.Set<LinenItem>().AsNoTracking().OrderBy(i => i.Name).ToListAsync(ct);
        var circulating = await db.Set<LinenHolding>().AsNoTracking().GroupBy(h => h.LinenItemId)
            .Select(g => new { g.Key, Pieces = g.Sum(h => h.Quantity) }).ToDictionaryAsync(g => g.Key, g => g.Pieces, ct);
        return items.Select(i => new LinenItemDto(i.Id, i.Code, i.Name, i.LifeWashes,
            circulating.GetValueOrDefault(i.Id) is > 0 and var pieces ? Math.Round((double)i.Washes / pieces, 1) : null, i.IsActive, i.Version)).ToList();
    }

    public async Task<Result<IReadOnlyList<LinenItemDto>>> SaveItemAsync(SaveLinenItemRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var code = request.Code.Trim().ToUpperInvariant();
        LinenItem? item;
        if (request.Id is { } id)
        {
            item = await db.Set<LinenItem>().FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Error.NotFound("Linen item", id);
            }

            db.SetExpectedVersion(item, request.Version!.Value);
        }
        else
        {
            item = new LinenItem { FacilityId = facility };
            db.Add(item);
        }

        if (await db.Set<LinenItem>().AnyAsync(i => i.Code == code && i.Id != item.Id, ct))
        {
            return Error.Conflict("linen.duplicate", $"{code} is already an item.");
        }

        item.Code = code;
        item.Name = request.Name.Trim();
        item.LifeWashes = request.LifeWashes;
        item.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<LinenItemDto>>.Ok(await ItemsAsync(ct));
    }

    /// <summary>The facility's wards in use, for choosing where linen goes.</summary>
    public async Task<IReadOnlyList<LinenWard>> WardsAsync(CancellationToken ct) =>
        await db.Set<Ward>().AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.Name).Select(w => new LinenWard(w.Id, w.Name)).ToListAsync(ct);

    public async Task<LinenStock> StockAsync(CancellationToken ct)
    {
        var items = await db.Set<LinenItem>().AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.Name).ToListAsync(ct);
        var holdings = await db.Set<LinenHolding>().AsNoTracking().ToListAsync(ct);
        var wardIds = holdings.Where(h => h.WardId is not null).Select(h => h.WardId!.Value).Distinct().ToList();
        var wards = await db.Set<Ward>().AsNoTracking().Where(w => wardIds.Contains(w.Id)).OrderBy(w => w.Name).ToDictionaryAsync(w => w.Id, w => w.Name, ct);

        int Sum(Guid item, Func<LinenHolding, bool> where) => holdings.Where(h => h.LinenItemId == item && where(h)).Sum(h => h.Quantity);

        var stock = items.Select(i => new LinenItemStock(
            i.Id, i.Code, i.Name,
            Sum(i.Id, h => h.Location == LinenLocation.Store),
            Sum(i.Id, h => h.Location == LinenLocation.Ward),
            Sum(i.Id, h => h.Location == LinenLocation.Laundry && !h.Infected),
            Sum(i.Id, h => h.Location == LinenLocation.Laundry && h.Infected),
            Sum(i.Id, _ => true))).ToList();
        var byWard = wards.Select(w => new WardLinen(w.Key, w.Value, items
            .Select(i => holdings.FirstOrDefault(h => h.Location == LinenLocation.Ward && h.WardId == w.Key && h.LinenItemId == i.Id) is { } h
                ? new WardLinenLine(i.Id, i.Name, h.Quantity, h.Par, Math.Max(0, h.Par - h.Quantity))
                : null)
            .OfType<WardLinenLine>().ToList())).ToList();
        return new LinenStock(stock, byWard);
    }

    public async Task<Result<LinenStock>> SetParAsync(SetParRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        if (!await db.Set<Ward>().AnyAsync(w => w.Id == request.WardId, ct))
        {
            return Error.Field("wardId", "Choose a ward of this facility.");
        }

        foreach (var line in request.Lines)
        {
            var holding = await HoldingAsync(facility, line.LinenItemId, LinenLocation.Ward, request.WardId, false, ct);
            holding.Par = line.Quantity;
        }

        await db.SaveChangesAsync(ct);
        return await StockAsync(ct);
    }

    public async Task<Result<LinenMovementDto>> RecordAsync(RecordLinenRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var ids = request.Lines.Select(l => l.LinenItemId).ToList();
        var items = await db.Set<LinenItem>().Where(i => ids.Contains(i.Id) && i.IsActive).ToDictionaryAsync(i => i.Id, ct);
        if (items.Count != ids.Count)
        {
            return Error.Field("lines", "Count items of the linen list that are in use.");
        }

        string? wardName = null;
        if (request.WardId is { } wardId)
        {
            wardName = await db.Set<Ward>().Where(w => w.Id == wardId).Select(w => w.Name).FirstOrDefaultAsync(ct);
            if (wardName is null)
            {
                return Error.Field("wardId", "Choose a ward of this facility.");
            }
        }

        var infected = request.Kind is LinenMovementKind.SoiledReturn or LinenMovementKind.Washed
            || (request.Kind == LinenMovementKind.Condemned && request.From == LinenLocation.Laundry) ? request.Infected : false;
        if (request.Kind == LinenMovementKind.Washed)
        {
            var wash = LinenMovement.CheckWash(infected, request.Process!.Value, request.TemperatureC, request.HoldMinutes);
            if (wash.IsFailure)
            {
                return wash.Error!;
            }
        }

        foreach (var line in request.Lines)
        {
            var item = items[line.LinenItemId];
            var moved = request.Kind switch
            {
                LinenMovementKind.Received => await PutAsync(facility, item, LinenLocation.Store, null, false, line.Quantity, ct),
                LinenMovementKind.Issued => await MoveAsync(facility, item, (LinenLocation.Store, null, false), (LinenLocation.Ward, request.WardId, false), line.Quantity, ct),
                LinenMovementKind.SoiledReturn => await MoveAsync(facility, item, (LinenLocation.Ward, request.WardId, false), (LinenLocation.Laundry, null, infected), line.Quantity, ct),
                LinenMovementKind.Washed => await MoveAsync(facility, item, (LinenLocation.Laundry, null, infected), (LinenLocation.Store, null, false), line.Quantity, ct),
                LinenMovementKind.Condemned => await TakeAsync(facility, item, request.From!.Value, null, infected, line.Quantity, ct),
                _ => await TakeAsync(facility, item, LinenLocation.Ward, request.WardId, false, line.Quantity, ct),
            };
            if (moved.IsFailure)
            {
                return moved.Error!;
            }

            if (request.Kind == LinenMovementKind.Washed)
            {
                item.Washes += line.Quantity;
            }
        }

        var movement = new LinenMovement
        {
            FacilityId = facility,
            Number = await numbers.NextAsync(SupportServicesNumberSeries.LinenMovement, facility, ct),
            Kind = request.Kind,
            WardId = request.WardId,
            WardName = wardName,
            Infected = infected,
            Process = request.Kind == LinenMovementKind.Washed ? request.Process : null,
            TemperatureC = request.Kind == LinenMovementKind.Washed ? request.TemperatureC : null,
            HoldMinutes = request.Kind == LinenMovementKind.Washed ? request.HoldMinutes : null,
            CondemnReason = request.Kind == LinenMovementKind.Condemned ? request.CondemnReason : null,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            At = time.GetUtcNow(),
            ByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
            Lines = request.Lines.Select(l => new LinenMovementLine { LinenItemId = l.LinenItemId, ItemName = items[l.LinenItemId].Name, Quantity = l.Quantity }).ToList(),
        };
        db.Add(movement);
        await db.SaveChangesAsync(ct);
        return Dto(movement);
    }

    public async Task<IReadOnlyList<LinenMovementDto>> MovementsAsync(DateOnly from, DateOnly to, LinenMovementKind? kind, CancellationToken ct)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
        var query = db.Set<LinenMovement>().AsNoTracking().Include(m => m.Lines).Where(m => m.At >= start && m.At < end);
        if (kind is not null)
        {
            query = query.Where(m => m.Kind == kind);
        }

        return (await query.OrderByDescending(m => m.At).Take(500).ToListAsync(ct)).Select(Dto).ToList();
    }

    private static LinenMovementDto Dto(LinenMovement m) => new(
        m.Id, m.Number, m.Kind, m.WardName, m.Infected, m.Process, m.TemperatureC, m.HoldMinutes, m.CondemnReason, m.Notes, m.At, m.ByName,
        m.Lines.Select(l => new LinenMovementLineDto(l.LinenItemId, l.ItemName, l.Quantity)).ToList(), m.Pieces);

    private async Task<Result> MoveAsync(
        Guid facility, LinenItem item, (LinenLocation Place, Guid? Ward, bool Infected) from, (LinenLocation Place, Guid? Ward, bool Infected) to, int quantity,
        CancellationToken ct)
    {
        var taken = await TakeAsync(facility, item, from.Place, from.Ward, from.Infected, quantity, ct);
        return taken.IsFailure ? taken : await PutAsync(facility, item, to.Place, to.Ward, to.Infected, quantity, ct);
    }

    private async Task<Result> TakeAsync(Guid facility, LinenItem item, LinenLocation place, Guid? ward, bool infected, int quantity, CancellationToken ct)
    {
        var holding = await HoldingAsync(facility, item.Id, place, ward, infected, ct);
        var name = place switch
        {
            LinenLocation.Ward => "ward",
            LinenLocation.Laundry => infected ? "laundry (infected)" : "laundry",
            _ => "store",
        };
        return holding.Take(quantity, item.Name, name);
    }

    private async Task<Result> PutAsync(Guid facility, LinenItem item, LinenLocation place, Guid? ward, bool infected, int quantity, CancellationToken ct)
    {
        (await HoldingAsync(facility, item.Id, place, ward, infected, ct)).Quantity += quantity;
        return Result.Success();
    }

    private async Task<LinenHolding> HoldingAsync(Guid facility, Guid itemId, LinenLocation place, Guid? ward, bool infected, CancellationToken ct)
    {
        var holding = db.ChangeTracker.Entries<LinenHolding>().Select(e => e.Entity)
                .FirstOrDefault(h => h.LinenItemId == itemId && h.Location == place && h.WardId == ward && h.Infected == infected)
            ?? await db.Set<LinenHolding>().FirstOrDefaultAsync(h => h.LinenItemId == itemId && h.Location == place && h.WardId == ward && h.Infected == infected, ct);
        if (holding is null)
        {
            holding = new LinenHolding { FacilityId = facility, LinenItemId = itemId, Location = place, WardId = ward, Infected = infected };
            db.Add(holding);
        }

        return holding;
    }
}
