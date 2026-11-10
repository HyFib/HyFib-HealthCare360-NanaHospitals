using HyFib.HealthCare360.Modules.Inpatient.Domain;
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

    /// <summary>The hospital's unique HCF identifier for the bar-code system (CPCB guidelines); the authorisation number is used when it is not set.</summary>
    public const string HcfIdSetting = "bmw.hcfId";

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

        var weighed = request.Bags.GroupBy(b => b.Barcode.Trim().ToUpperInvariant()).ToList();
        if (weighed.FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return Error.Field("bags", $"Bag {twice.Key} is scanned twice: weigh each bag once.");
        }

        var weights = weighed.ToDictionary(g => g.Key, g => g.Single().WeightKg);
        var barcodes = weights.Keys.ToList();
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
            LateBags = bags.Count(b => b.IsOverdue(now)),
        };
        foreach (var bag in bags)
        {
            var handed = bag.HandOver(handover.Id, weights[bag.Barcode]);
            if (handed.IsFailure)
            {
                return handed.Error!;
            }
        }

        handover.TotalKg = bags.Sum(b => b.HandoverWeightKg ?? b.WeightKg);
        handover.MismatchedBags = bags.Count(b => b.WeightMismatch);

        db.Add(handover);
        await db.SaveChangesAsync(ct);
        return HandoverDto(handover, bags, now);
    }

    public async Task<IReadOnlyList<WasteHandoverDto>> HandoversAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (start, end) = (Start(from), Start(to.AddDays(1)));
        var handovers = await db.Set<WasteHandover>().AsNoTracking().Where(h => h.HandedOverAt >= start && h.HandedOverAt < end)
            .OrderByDescending(h => h.HandedOverAt).Take(500).ToListAsync(ct);
        var ids = handovers.Select(h => h.Id).ToList();
        var bags = await db.Set<WasteBag>().AsNoTracking().Where(b => b.HandoverId != null && ids.Contains(b.HandoverId.Value)).ToListAsync(ct);
        var now = time.GetUtcNow();
        return handovers.Select(h => HandoverDto(h, bags.Where(b => b.HandoverId == h.Id).ToList(), now)).ToList();
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
        b.VoidReason, b.Version)
    {
        HandoverWeightKg = b.HandoverWeightKg,
        WeightMismatch = b.WeightMismatch,
    };

    private static WasteHandoverDto HandoverDto(WasteHandover h, IReadOnlyCollection<WasteBag> bags, DateTimeOffset now) => new(
        h.Id, h.Number, h.HandedOverAt, h.Cbwtf, h.VehicleNumber, h.ReceivedBy, h.ReceiptNumber, h.HandedOverByName, h.BagCount, h.TotalKg, h.LateBags,
        HandedOverTotals(bags))
    {
        MismatchedBags = h.MismatchedBags,
        Bags = bags.OrderBy(b => b.Barcode, StringComparer.Ordinal).Select(b => Dto(b, now)).ToList(),
    };

    /// <summary>By category, on the weights at handover (what the CBWTF signed for).</summary>
    private static IReadOnlyList<WasteCategoryTotal> HandedOverTotals(IReadOnlyCollection<WasteBag> bags) =>
        Enum.GetValues<WasteCategory>()
            .Select(c => new WasteCategoryTotal(c, bags.Count(b => b.Category == c), bags.Where(b => b.Category == c).Sum(b => b.HandoverWeightKg ?? b.WeightKg)))
            .ToList();

    /// <summary>The bag's label (Schedule IV): refused until the hospital's HCF identifier or its authorisation is set, which every label must carry.</summary>
    public async Task<Result<WasteBagLabel>> LabelAsync(Guid id, CancellationToken ct)
    {
        var bag = await db.Set<WasteBag>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bag is null)
        {
            return Error.NotFound("Bag", id);
        }

        var authorisation = await Setting(AuthorisationSetting, bag.FacilityId, ct);
        var hcfId = await Setting(HcfIdSetting, bag.FacilityId, ct) ?? authorisation;
        if (hcfId is null)
        {
            return Error.Rule("bmw.hcf_id_required",
                $"Every bag's label carries the hospital's HCF identifier. Set it ({HcfIdSetting}), or the authorisation number ({AuthorisationSetting}), under Administration › Settings.");
        }

        var name = await db.Set<Facility>().Where(f => f.Id == bag.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        return new WasteBagLabel(bag.Barcode, bag.Category, WasteBag.Colour(bag.Category), WasteBag.Symbol(bag.Category), bag.Source, bag.WeightKg,
            DateOnly.FromDateTime(bag.CollectedAt.ToOffset(India).DateTime), hcfId, name, authorisation);
    }

    public async Task<Result<WasteAccidentDto>> RecordAccidentAsync(RecordWasteAccidentRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var accident = new WasteAccident
        {
            FacilityId = facility,
            OccurredAt = request.OccurredAt,
            Kind = request.Kind,
            Place = request.Place.Trim(),
            Sequence = request.Sequence.Trim(),
            Category = request.Category,
            WasteInvolved = request.WasteInvolved.Trim(),
            PersonsAffected = request.PersonsAffected,
            Effects = request.Effects.Trim(),
            EmergencyMeasures = request.EmergencyMeasures.Trim(),
            StepsToAlleviate = Clean(request.StepsToAlleviate),
            StepsToPrevent = Clean(request.StepsToPrevent),
            AuthorityInformedAt = request.AuthorityInformedAt,
            ReportedAt = time.GetUtcNow(),
            ReportedBy = currentUser.UserId,
            ReportedByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
            ReportedByDesignation = Clean(request.Designation),
        };
        db.Add(accident);
        await db.SaveChangesAsync(ct);
        return AccidentDto(accident);
    }

    public async Task<IReadOnlyList<WasteAccidentDto>> AccidentsAsync(int year, CancellationToken ct)
    {
        var (start, end) = (Start(new DateOnly(year, 1, 1)), Start(new DateOnly(year + 1, 1, 1)));
        var accidents = await db.Set<WasteAccident>().AsNoTracking().Where(a => a.OccurredAt >= start && a.OccurredAt < end)
            .OrderByDescending(a => a.OccurredAt).Take(500).ToListAsync(ct);
        return accidents.Select(AccidentDto).ToList();
    }

    /// <summary>Form I for one accident, every item of the form answered from the record.</summary>
    public async Task<Result<FormI>> FormIAsync(Guid id, CancellationToken ct)
    {
        var accident = await db.Set<WasteAccident>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (accident is null)
        {
            return Error.NotFound("Accident", id);
        }

        var (name, address) = await FacilityAsync(accident.FacilityId, ct);
        var authorisation = await Setting(AuthorisationSetting, accident.FacilityId, ct);
        var hcfId = await Setting(HcfIdSetting, accident.FacilityId, ct);
        return new FormI(name, address, authorisation, hcfId, AccidentDto(accident), WasteForms.FormILines(accident, India));
    }

    /// <summary>Form IV for a calendar year, from the bags, the handovers and the accidents on record.</summary>
    public async Task<FormIv> FormIvAsync(int year, CancellationToken ct)
    {
        var facility = currentUser.FacilityId ?? Guid.Empty;
        var start = Start(new DateOnly(year, 1, 1));
        var end = Start(new DateOnly(year + 1, 1, 1));
        var generated = await Generated(start, end).ToListAsync(ct);
        var handovers = await db.Set<WasteHandover>().AsNoTracking().Where(h => h.HandedOverAt >= start && h.HandedOverAt < end).ToListAsync(ct);
        var handoverIds = handovers.Select(h => h.Id).ToList();
        var handed = await db.Set<WasteBag>().AsNoTracking().Where(b => b.HandoverId != null && handoverIds.Contains(b.HandoverId.Value)).ToListAsync(ct);
        var accidents = await db.Set<WasteAccident>().AsNoTracking().Where(a => a.OccurredAt >= start && a.OccurredAt < end).ToListAsync(ct);
        var beds = await db.Set<Bed>().AsNoTracking().CountAsync(b => b.IsActive, ct);
        var (name, address) = await FacilityAsync(facility, ct);
        var authorisation = await Setting(AuthorisationSetting, facility, ct);
        var cbwtf = await Setting(CbwtfSetting, facility, ct);
        var days = new DateOnly(year, 12, 31).DayNumber - new DateOnly(year, 1, 1).DayNumber + 1;
        var totals = Totals(generated);
        var lines = WasteForms.FormIvLines(year, name, address, authorisation, beds, totals, generated.Sum(b => b.WeightKg), days, cbwtf, handovers.Count,
            HandedOverTotals(handed), handovers.Sum(h => h.LateBags), handovers.Sum(h => h.MismatchedBags), accidents);
        return new FormIv(year, name, address, authorisation, await Setting(HcfIdSetting, facility, ct), cbwtf, beds, lines, totals, HandedOverTotals(handed));
    }

    private async Task<(string Name, string? Address)> FacilityAsync(Guid facility, CancellationToken ct)
    {
        var row = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facility).Select(f => new { f.Name, f.Address }).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return (string.Empty, null);
        }

        var address = row.Address.ToString();
        return (row.Name, address.Length == 0 ? null : address);
    }

    private static WasteAccidentDto AccidentDto(WasteAccident a) => new(
        a.Id, a.OccurredAt, a.Kind, a.Place, a.Sequence, a.Category, a.WasteInvolved, a.PersonsAffected, a.Effects, a.EmergencyMeasures, a.StepsToAlleviate,
        a.StepsToPrevent, a.AuthorityInformedAt, a.ReportedAt, a.ReportedByName, a.ReportedByDesignation);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
