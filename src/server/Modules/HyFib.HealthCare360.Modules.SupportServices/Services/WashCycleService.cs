using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Washer-disinfector cycles (CSSD-04), numbered "{FAC}CW{YY}{SEQ:6}": the trays washed, the disinfection the washer's
/// printout shows and the inspection afterwards. Sterilizer loads name the passed cycle their packs were washed in, and a
/// cycle lists the packs made from its trays.
/// </summary>
public sealed class WashCycleService(HospitalDbContext db, INumberSeries numbers, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<Result<WashCycleDto>> StartAsync(StartWashCycleRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var setIds = request.Trays.Select(t => t.InstrumentSetId).Distinct().ToList();
        var known = await db.Set<InstrumentSet>().AsNoTracking().CountAsync(s => setIds.Contains(s.Id) && s.IsActive, ct);
        if (known != setIds.Count)
        {
            return Error.Field("trays", "Every tray must be an active instrument set.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var cycle = new WashCycle
        {
            FacilityId = facilityId,
            CycleNumber = await numbers.NextAsync(SupportServicesNumberSeries.WashCycle, facilityId, ct),
            WasherName = request.WasherName.Trim(),
            Program = Clean(request.Program),
            StartedAt = time.GetUtcNow(),
            OperatorName = Clean(request.OperatorName) ?? currentUser.DisplayName,
            OperatorUserId = currentUser.UserId,
            Status = WashCycleStatus.Running,
            Notes = Clean(request.Notes),
        };
        foreach (var trays in request.Trays.GroupBy(t => t.InstrumentSetId))
        {
            cycle.Trays.Add(new WashCycleTray { WashCycleId = cycle.Id, InstrumentSetId = trays.Key, Quantity = trays.Sum(t => t.Quantity) });
        }

        db.Add(cycle);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ToDtoAsync(cycle, ct);
    }

    public Task<Result<WashCycleDto>> CompleteAsync(Guid id, CompleteWashCycleRequest request, CancellationToken ct) =>
        ChangeAsync(id, (cycle, now) => cycle.Complete(request.TemperatureC, request.HoldMinutes, request.InspectedClean, now), ct);

    public Task<Result<WashCycleDto>> AbortAsync(Guid id, string reason, CancellationToken ct) =>
        ChangeAsync(id, (cycle, now) => cycle.Abort(reason, now), ct);

    public async Task<Result<WashCycleDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var cycle = await db.Set<WashCycle>().AsNoTracking().Include(c => c.Trays).FirstOrDefaultAsync(c => c.Id == id, ct);
        return cycle is null ? Error.NotFound("Wash cycle", id) : await ToDtoAsync(cycle, ct);
    }

    private async Task<Result<WashCycleDto>> ChangeAsync(Guid id, Func<WashCycle, DateTimeOffset, Result> change, CancellationToken ct)
    {
        var cycle = await db.Set<WashCycle>().Include(c => c.Trays).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cycle is null)
        {
            return Error.NotFound("Wash cycle", id);
        }

        var result = change(cycle, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(cycle, ct);
    }

    private async Task<WashCycleDto> ToDtoAsync(WashCycle cycle, CancellationToken ct)
    {
        var setIds = cycle.Trays.Select(t => t.InstrumentSetId).ToList();
        var setNames = await db.Set<InstrumentSet>().AsNoTracking().Where(s => setIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var packs = await (
                from p in db.Set<SterilePack>().AsNoTracking()
                join l in db.Set<SterilizationLoad>() on p.LoadId equals l.Id
                join s in db.Set<InstrumentSet>() on p.InstrumentSetId equals s.Id
                where p.WashCycleId == cycle.Id
                orderby p.PackNumber
                select new WashPackDto(p.Id, p.PackNumber, p.LoadId, l.CycleNumber, s.Name, p.Status))
            .ToListAsync(ct);

        return new WashCycleDto(
            cycle.Id, cycle.CycleNumber, cycle.WasherName, cycle.Program, cycle.Status, cycle.StartedAt, cycle.CompletedAt,
            cycle.DisinfectionTemperatureC, cycle.HoldMinutes, cycle.A0, WashCycle.RequiredA0, cycle.InspectedClean, cycle.FailureReason,
            cycle.OperatorName, cycle.Notes,
            cycle.Trays.Select(t => new WashTrayDto(t.InstrumentSetId, setNames.GetValueOrDefault(t.InstrumentSetId), t.Quantity))
                .OrderBy(t => t.SetName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            packs,
            cycle.Version);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
