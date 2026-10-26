using HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;
using HyFib.HealthCare360.Modules.SupportServices.Features.Waste;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Bio-medical waste (BMW Management Rules 2016, as amended in 2018): bags bar-coded and weighed where the waste is
/// generated, handed over to the common treatment facility within 48 hours, the month's register the hospital publishes,
/// and the year's totals for the annual report (Form IV).
/// </summary>
internal sealed class WasteService(HospitalDbContext db, INumberSeries numbers, ISettingsService settings, ICurrentUser currentUser, TimeProvider time)
{
    /// <summary>The authorisation the State Pollution Control Board granted the hospital (Rule 10, Form III).</summary>
    public const string AuthorisationSetting = "bmw.authorisationNumber";

    /// <summary>The common bio-medical waste treatment facility the hospital is tied up with.</summary>
    public const string CbwtfSetting = "bmw.cbwtf";

    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    public async Task<Result<WasteBagDto>> RecordAsync(RecordWasteBagRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var now = time.GetUtcNow();
        var collectedAt = request.CollectedAt ?? now;
        var valid = WasteBag.Validate(request.WeightKg, collectedAt, now);
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        var source = request.Source?.Trim();
        if (request.DepartmentId is { } departmentId)
        {
            source = await db.Set<Department>().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct);
            if (source is null)
            {
                return Error.Field("departmentId", "Choose a department of this hospital.");
            }
        }

        var bag = new WasteBag
        {
            FacilityId = facility,
            Barcode = await numbers.NextAsync(SupportServicesNumberSeries.WasteBag, facility, ct),
            Category = request.Category,
            DepartmentId = request.DepartmentId,
            Source = source!,
            WeightKg = Math.Round(request.WeightKg, 3),
            CollectedAt = collectedAt,
            CollectedBy = currentUser.UserId,
            CollectedByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
        };
        db.Add(bag);
        await db.SaveChangesAsync(ct);
        return Dto(bag, now);
    }

    public async Task<Result<WasteBagDto>> VoidAsync(Guid id, VoidWasteBagRequest request, CancellationToken ct)
    {
        var bag = await db.Set<WasteBag>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bag is null)
        {
            return Error.NotFound("Bag", id);
        }

        db.SetExpectedVersion(bag, request.Version);
        var voided = bag.Void(request.Reason);
        if (voided.IsFailure)
        {
            return voided.Error!;
        }

        await db.SaveChangesAsync(ct);
        return Dto(bag, time.GetUtcNow());
    }

    public async Task<PendingWaste> PendingAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var bags = await db.Set<WasteBag>().AsNoTracking().Where(b => b.Status == WasteBagStatus.Collected).OrderBy(b => b.CollectedAt).ToListAsync(ct);
        return new PendingWaste(bags.Select(b => Dto(b, now)).ToList(), Totals(bags), bags.Count(b => b.IsOverdue(now)));
    }

    public async Task<IReadOnlyList<WasteBagDto>> BagsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (start, end) = (Start(from), Start(to.AddDays(1)));
        var now = time.GetUtcNow();
        var bags = await db.Set<WasteBag>().AsNoTracking().Where(b => b.CollectedAt >= start && b.CollectedAt < end)
            .OrderByDescending(b => b.CollectedAt).Take(2000).ToListAsync(ct);
        return bags.Select(b => Dto(b, now)).ToList();
    }

    /// <summary>
    /// Hands the scanned bags to the CBWTF's vehicle. Every bag must be in the storage area; the bags kept beyond
    /// 48 hours are counted on the handover, so the breach is on record.
    /// </summary>
    public async Task<Result<WasteHandoverDto>> HandOverAsync(HandOverWasteRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var barcodes = request.Barcodes.Select(b => b.Trim().ToUpperInvariant()).Distinct().ToList();
        var bags = await db.Set<WasteBag>().Where(b => barcodes.Contains(b.Barcode)).ToListAsync(ct);
        var unknown = barcodes.Except(bags.Select(b => b.Barcode)).ToList();
        if (unknown.Count > 0)
        {
            return Error.Field("barcodes", $"No such bag: {string.Join(", ", unknown)}.");
        }

        var cbwtf = string.IsNullOrWhiteSpace(request.Cbwtf) ? await settings.GetAsync<string?>(CbwtfSetting, facility, ct) : request.Cbwtf.Trim();
        if (string.IsNullOrWhiteSpace(cbwtf))
        {
            return Error.Field("cbwtf", $"Name the treatment facility, or set it once ({CbwtfSetting}) under Administration › Settings.");
        }

        var now = time.GetUtcNow();
        var handover = new WasteHandover
        {
            FacilityId = facility,
            Number = await numbers.NextAsync(SupportServicesNumberSeries.WasteHandover, facility, ct),
            HandedOverAt = now,
            Cbwtf = cbwtf.Trim(),
            VehicleNumber = request.VehicleNumber.Trim().ToUpperInvariant(),
            ReceivedBy = request.ReceivedBy.Trim(),
            ReceiptNumber = string.IsNullOrWhiteSpace(request.ReceiptNumber) ? null : request.ReceiptNumber.Trim(),
            HandedOverBy = currentUser.UserId,
            HandedOverByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
            BagCount = bags.Count,
            TotalKg = bags.Sum(b => b.WeightKg),
            LateBags = bags.Count(b => b.IsOverdue(now)),
        };
        foreach (var bag in bags)
        {
            var handed = bag.HandOver(handover.Id);
            if (handed.IsFailure)
            {
                return handed.Error!;
            }
        }

        db.Add(handover);
        await db.SaveChangesAsync(ct);
        return HandoverDto(handover, bags);
    }

    public async Task<IReadOnlyList<WasteHandoverDto>> HandoversAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (start, end) = (Start(from), Start(to.AddDays(1)));
        var handovers = await db.Set<WasteHandover>().AsNoTracking().Where(h => h.HandedOverAt >= start && h.HandedOverAt < end)
            .OrderByDescending(h => h.HandedOverAt).Take(500).ToListAsync(ct);
        var ids = handovers.Select(h => h.Id).ToList();
        var bags = await db.Set<WasteBag>().AsNoTracking().Where(b => b.HandoverId != null && ids.Contains(b.HandoverId.Value)).ToListAsync(ct);
        return handovers.Select(h => HandoverDto(h, bags.Where(b => b.HandoverId == h.Id).ToList())).ToList();
    }

    /// <summary>The month's register: what each day generated, by category, without voided bags.</summary>
    public async Task<WasteMonth> MonthAsync(int year, int month, CancellationToken ct)
    {
        var first = new DateOnly(year, month, 1);
        var bags = await Generated(Start(first), Start(first.AddMonths(1))).ToListAsync(ct);
        var days = bags.GroupBy(b => DateOnly.FromDateTime(b.CollectedAt.ToOffset(India).DateTime)).OrderBy(g => g.Key)
            .Select(g => new WasteDay(g.Key, Totals(g.ToList()), g.Sum(b => b.WeightKg))).ToList();
        var handovers = await db.Set<WasteHandover>().AsNoTracking()
            .Where(h => h.HandedOverAt >= Start(first) && h.HandedOverAt < Start(first.AddMonths(1))).ToListAsync(ct);
        return new WasteMonth(year, month, days, Totals(bags), handovers.Count, handovers.Sum(h => h.LateBags));
    }

    /// <summary>The year's totals for Form IV, with the authorisation and the CBWTF as set for the facility.</summary>
    public async Task<WasteYear> YearAsync(int year, CancellationToken ct)
    {
        var start = Start(new DateOnly(year, 1, 1));
        var end = Start(new DateOnly(year + 1, 1, 1));
        var bags = await Generated(start, end).ToListAsync(ct);
        var voided = await db.Set<WasteBag>().AsNoTracking().CountAsync(b => b.Status == WasteBagStatus.Voided && b.CollectedAt >= start && b.CollectedAt < end, ct);
        var handovers = await db.Set<WasteHandover>().AsNoTracking().Where(h => h.HandedOverAt >= start && h.HandedOverAt < end).ToListAsync(ct);
        var facility = currentUser.FacilityId;
        var total = bags.Sum(b => b.WeightKg);
        var days = new DateOnly(year, 12, 31).DayNumber - new DateOnly(year, 1, 1).DayNumber + 1;
        return new WasteYear(
            year, await Setting(AuthorisationSetting, facility, ct), await Setting(CbwtfSetting, facility, ct), Totals(bags), total,
            Math.Round(total / days, 3), handovers.Count, handovers.Sum(h => h.LateBags), voided);
    }

    private async Task<string?> Setting(string key, Guid? facility, CancellationToken ct) =>
        await settings.GetAsync<string?>(key, facility, ct) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private IQueryable<WasteBag> Generated(DateTimeOffset start, DateTimeOffset end) =>
        db.Set<WasteBag>().AsNoTracking().Where(b => b.Status != WasteBagStatus.Voided && b.CollectedAt >= start && b.CollectedAt < end);

    private static DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), India);

    private static IReadOnlyList<WasteCategoryTotal> Totals(IReadOnlyCollection<WasteBag> bags) =>
        Enum.GetValues<WasteCategory>()
            .Select(c => new WasteCategoryTotal(c, bags.Count(b => b.Category == c), bags.Where(b => b.Category == c).Sum(b => b.WeightKg)))
            .ToList();

    private static WasteBagDto Dto(WasteBag b, DateTimeOffset now) => new(
        b.Id, b.Barcode, b.Category, b.DepartmentId, b.Source, b.WeightKg, b.CollectedAt, b.CollectedByName, b.Status, b.IsOverdue(now), b.HandoverId,
        b.VoidReason, b.Version);

    private static WasteHandoverDto HandoverDto(WasteHandover h, IReadOnlyCollection<WasteBag> bags) => new(
        h.Id, h.Number, h.HandedOverAt, h.Cbwtf, h.VehicleNumber, h.ReceivedBy, h.ReceiptNumber, h.HandedOverByName, h.BagCount, h.TotalKg, h.LateBags,
        Totals(bags));
}
