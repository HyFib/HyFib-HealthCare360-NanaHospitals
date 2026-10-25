using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Ndps;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// The NDPS accounts of a recognised medical institution, read from the controlled-drug register (PH-06), which holds
/// every movement of a narcotic with its running balance: each drug's day in a store (Form 3H), a patient's record
/// (Form 3E), the year's return and next year's estimate (Form 3J); and the two-person count at a change of shift.
/// </summary>
internal sealed class NdpsService(HospitalDbContext db, IOutbox outbox, ICurrentUser currentUser, TimeProvider time)
{
    internal const string AlertType = "pharmacy.ndps-alert";

    private static readonly StockMovementType[] Receipts = [StockMovementType.Grn, StockMovementType.Opening];

    private static readonly StockMovementType[] Dispensing =
        [StockMovementType.Dispense, StockMovementType.Sale, StockMovementType.Issue, StockMovementType.Consumption];

    private static readonly StockMovementType[] Transfers = [StockMovementType.TransferIn, StockMovementType.TransferOut];

    public async Task<Result<NdpsDailyAccount>> DailyAsync(Guid storeId, DateOnly date, CancellationToken ct)
    {
        if (await db.Set<Store>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct) is not { } store)
        {
            return Error.NotFound(nameof(Store), storeId);
        }

        var start = HospitalClock.StartOfDayUtc(date);
        var end = HospitalClock.StartOfDayUtc(date.AddDays(1));
        var drugs = await EndItemsAsync(db.Set<ControlledDrugRegisterEntry>().Where(r => r.StoreId == storeId && r.OccurredAt < end), ct);
        var ids = drugs.Keys.ToList();
        var day = await Entries().Where(r => r.StoreId == storeId && ids.Contains(r.ItemId) && r.OccurredAt >= start && r.OccurredAt < end)
            .OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).ToListAsync(ct);
        var openings = await BalancesAsync(storeId, ids, start, ct);

        var rows = drugs.Values.OrderBy(i => i.Name).Select(item =>
        {
            var lines = day.Where(r => r.ItemId == item.Id).ToList();
            var opening = openings.GetValueOrDefault(item.Id);
            var received = lines.Where(r => Receipts.Contains(r.MovementType) || r.MovementType == StockMovementType.TransferIn).Sum(r => r.QuantityIn);
            var dispensed = lines.Where(r => Dispensing.Contains(r.MovementType)).Sum(r => r.QuantityOut);
            var otherIn = lines.Sum(r => r.QuantityIn) - received;
            var otherOut = lines.Sum(r => r.QuantityOut) - dispensed;
            var closing = opening + received + otherIn - dispensed - otherOut;
            var register = lines.Count == 0 ? opening : lines[^1].BalanceAfter;
            return new NdpsDailyDrug(
                item.Id, item.Name, opening, received, dispensed, otherIn, otherOut, closing, closing == register,
                lines.Select(r => new NdpsMovement(
                    r.OccurredAt, r.MovementType, r.QuantityIn, r.QuantityOut, r.BalanceAfter, r.ReferenceNumber, r.PatientUhid, r.PatientName,
                    r.PrescriberName, r.PrescriptionNumber)).ToList());
        }).ToList();
        return new NdpsDailyAccount(date, store.Id, store.Name, rows);
    }

    /// <summary>
    /// The year, January to December, for the institution as a whole: transfers between its stores cancel out, so
    /// received is what came from suppliers and consumed is what patients were given less what they returned.
    /// </summary>
    public async Task<NdpsAnnualReturn> AnnualAsync(int year, CancellationToken ct)
    {
        var start = HospitalClock.StartOfDayUtc(new DateOnly(year, 1, 1));
        var end = HospitalClock.StartOfDayUtc(new DateOnly(year + 1, 1, 1));
        var drugs = await EndItemsAsync(db.Set<ControlledDrugRegisterEntry>().Where(r => r.OccurredAt < end), ct);
        var ids = drugs.Keys.ToList();
        var lines = await Entries().Where(r => ids.Contains(r.ItemId) && r.OccurredAt >= start && r.OccurredAt < end).ToListAsync(ct);
        var openings = await BalancesAsync(null, ids, start, ct);
        var closings = await BalancesAsync(null, ids, end, ct);

        var rows = drugs.Values.OrderBy(i => i.Name).Select(item =>
        {
            var mine = lines.Where(r => r.ItemId == item.Id && !Transfers.Contains(r.MovementType)).ToList();
            var received = mine.Where(r => Receipts.Contains(r.MovementType)).Sum(r => r.QuantityIn);
            var consumed = mine.Where(r => Dispensing.Contains(r.MovementType)).Sum(r => r.QuantityOut)
                           - mine.Where(r => r.MovementType == StockMovementType.Return).Sum(r => r.QuantityIn);
            var writtenOff = mine.Where(r => !Receipts.Contains(r.MovementType) && !Dispensing.Contains(r.MovementType) && r.MovementType != StockMovementType.Return)
                .Sum(r => r.QuantityOut - r.QuantityIn);
            var opening = openings.GetValueOrDefault(item.Id);
            var closing = closings.GetValueOrDefault(item.Id);
            return new NdpsAnnualDrug(
                item.Id, item.Name, opening, received, consumed, writtenOff, closing, opening + received - consumed - writtenOff == closing,
                EssentialNarcoticDrugs.Estimate(consumed));
        }).ToList();
        return new NdpsAnnualReturn(year, rows);
    }

    public async Task<Result<NdpsPatientRecord>> PatientAsync(Guid patientId, CancellationToken ct)
    {
        if (await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct) is not { } patient)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        var drugs = await EndItemsAsync(db.Set<ControlledDrugRegisterEntry>().Where(r => r.PatientId == patientId), ct);
        var ids = drugs.Keys.ToList();
        var lines = await Entries().Where(r => r.PatientId == patientId && ids.Contains(r.ItemId))
            .OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).ToListAsync(ct);
        var today = HospitalClock.Today(time);
        int? age = patient.DateOfBirth is { } dob ? today.Year - dob.Year - (today < dob.AddYears(today.Year - dob.Year) ? 1 : 0) : null;
        return new NdpsPatientRecord(
            patient.Id, patient.FullName, patient.Uhid, patient.Gender.ToString(), age, patient.Address.ToString(),
            lines.Select(r => new NdpsPatientLine(
                r.OccurredAt, r.ItemName, r.BatchNumber, r.QuantityOut - r.QuantityIn, r.MovementType, r.PrescriberName, r.PrescriberRegistrationNumber,
                r.PrescriptionNumber, r.ReferenceNumber)).ToList());
    }

    /// <summary>The stores that have held an essential narcotic drug: where counts are made.</summary>
    public async Task<IReadOnlyList<NdpsStore>> StoresAsync(CancellationToken ct)
    {
        var drugs = await EndItemsAsync(db.Set<ControlledDrugRegisterEntry>(), ct);
        var ids = drugs.Keys.ToList();
        var storeIds = await Entries().Where(r => ids.Contains(r.ItemId)).Select(r => r.StoreId).Distinct().ToListAsync(ct);
        return await db.Set<Store>().AsNoTracking().Where(s => storeIds.Contains(s.Id)).OrderBy(s => s.Name)
            .Select(s => new NdpsStore(s.Id, s.Name)).ToListAsync(ct);
    }

    /// <summary>The essential narcotic drugs the store has held: what a count must cover.</summary>
    public async Task<Result<NarcoticCountSheet>> SheetAsync(Guid storeId, CancellationToken ct)
    {
        if (await db.Set<Store>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct) is not { } store)
        {
            return Error.NotFound(nameof(Store), storeId);
        }

        var drugs = await EndItemsAsync(db.Set<ControlledDrugRegisterEntry>().Where(r => r.StoreId == storeId), ct);
        return new NarcoticCountSheet(store.Id, store.Name, drugs.Values.OrderBy(i => i.Name).Select(i => new NarcoticCountSheetItem(i.Id, i.Name)).ToList());
    }

    /// <summary>
    /// Records a count. The register balance is taken now, by the server; every drug the store holds must be counted,
    /// so a missing ampoule cannot hide in a drug left off the count.
    /// </summary>
    public async Task<Result<NarcoticCountDto>> CountAsync(RecordNarcoticCountRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var sheet = await SheetAsync(request.StoreId, ct);
        if (sheet.IsFailure)
        {
            return sheet.Error!;
        }

        var drugs = sheet.Value.Items.ToDictionary(i => i.ItemId);
        var unknown = request.Lines.Where(l => !drugs.ContainsKey(l.ItemId)).ToList();
        if (unknown.Count > 0)
        {
            return Error.Field("lines", "Count only the essential narcotic drugs this store holds.");
        }

        var balances = await BalancesAsync(request.StoreId, drugs.Keys.ToList(), time.GetUtcNow().AddTicks(1), ct);
        var missing = drugs.Values.Where(d => balances.GetValueOrDefault(d.ItemId) != 0 && request.Lines.All(l => l.ItemId != d.ItemId)).ToList();
        if (missing.Count > 0)
        {
            return Error.Field("lines", $"Count every drug the store holds: {string.Join(", ", missing.Select(m => m.ItemName))}.");
        }

        var count = new NarcoticCount
        {
            FacilityId = facility,
            StoreId = request.StoreId,
            CountedAt = time.GetUtcNow(),
            CountedBy = currentUser.UserId,
            CountedByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
            Explanation = string.IsNullOrWhiteSpace(request.Explanation) ? null : request.Explanation.Trim(),
            Lines = request.Lines.Select(l => new NarcoticCountLine
            {
                ItemId = l.ItemId,
                ItemName = drugs[l.ItemId].ItemName,
                RegisterBalance = balances.GetValueOrDefault(l.ItemId),
                Counted = l.Counted,
            }).ToList(),
        };
        db.Add(count);
        await db.SaveChangesAsync(ct);
        return Dto(count, sheet.Value.StoreName);
    }

    public async Task<Result<NarcoticCountDto>> WitnessAsync(Guid id, WitnessNarcoticCountRequest request, CancellationToken ct)
    {
        var count = await db.Set<NarcoticCount>().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (count is null)
        {
            return Error.NotFound("Count", id);
        }

        db.SetExpectedVersion(count, request.Version);
        var now = time.GetUtcNow();
        var witnessed = count.Witness(currentUser.UserId, currentUser.DisplayName ?? currentUser.UserName ?? "Staff", request.Explanation, now);
        if (witnessed.IsFailure)
        {
            return witnessed.Error!;
        }

        var store = await db.Set<Store>().AsNoTracking().Where(s => s.Id == count.StoreId).Select(s => s.Name).FirstAsync(ct);
        if (count.HasDifference)
        {
            var drugs = string.Join(", ", count.Lines.Where(l => l.Difference != 0).Select(l => $"{l.ItemName} {l.Difference:+0.###;-0.###}"));
            outbox.Enqueue(AlertType, new NdpsAlert(count.FacilityId, $"Narcotic count differs: {store}",
                $"{drugs}. Counted by {count.CountedByName}, witnessed by {count.WitnessedByName}: {count.Explanation}", "/pharmacy/ndps?tab=counts"),
                count.FacilityId, expiresIn: TimeSpan.FromDays(2));
        }

        await db.SaveChangesAsync(ct);
        return Dto(count, store);
    }

    public async Task<IReadOnlyList<NarcoticCountDto>> CountsAsync(Guid? storeId, CancellationToken ct)
    {
        var query = db.Set<NarcoticCount>().AsNoTracking().Include(c => c.Lines).AsQueryable();
        if (storeId is not null)
        {
            query = query.Where(c => c.StoreId == storeId);
        }

        var counts = await query.OrderByDescending(c => c.CountedAt).Take(50).ToListAsync(ct);
        var storeIds = counts.Select(c => c.StoreId).Distinct().ToList();
        var stores = await db.Set<Store>().AsNoTracking().Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return counts.Select(c => Dto(c, stores.GetValueOrDefault(c.StoreId) ?? string.Empty)).ToList();
    }

    private static NarcoticCountDto Dto(NarcoticCount c, string store) => new(
        c.Id, c.StoreId, store, c.CountedAt, c.CountedByName, c.Status, c.WitnessedAt, c.WitnessedByName, c.Explanation, c.HasDifference,
        c.Lines.OrderBy(l => l.ItemName).Select(l => new NarcoticCountLineDto(l.ItemId, l.ItemName, l.RegisterBalance, l.Counted, l.Difference)).ToList(),
        c.Version);

    private IQueryable<ControlledDrugRegisterEntry> Entries() => db.Set<ControlledDrugRegisterEntry>().AsNoTracking();

    /// <summary>The essential narcotic drugs among the items of these register lines, by id.</summary>
    private async Task<Dictionary<Guid, Item>> EndItemsAsync(IQueryable<ControlledDrugRegisterEntry> lines, CancellationToken ct)
    {
        var ids = await lines.Select(r => r.ItemId).Distinct().ToListAsync(ct);
        var items = await db.Set<Item>().AsNoTracking().Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        return items.Where(i => EssentialNarcoticDrugs.Is(i.GenericName)).ToDictionary(i => i.Id);
    }

    /// <summary>
    /// Each drug's register balance just before <paramref name="before"/>: in one store, or the sum over the
    /// facility's stores. The register keeps each line's balance of the item in its store.
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> BalancesAsync(Guid? storeId, IReadOnlyCollection<Guid> itemIds, DateTimeOffset before, CancellationToken ct)
    {
        var lines = Entries().Where(r => itemIds.Contains(r.ItemId) && r.OccurredAt < before);
        if (storeId is not null)
        {
            lines = lines.Where(r => r.StoreId == storeId);
        }

        var last = await lines
            .GroupBy(r => new { r.StoreId, r.ItemId })
            .Select(g => g.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id).Select(r => new { r.ItemId, r.BalanceAfter }).First())
            .ToListAsync(ct);
        return last.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.BalanceAfter));
    }
}

/// <summary>The alert to the pharmacist in charge of a count that differs from the register, delivered after the save.</summary>
internal sealed record NdpsAlert(Guid FacilityId, string Title, string Body, string Link);

internal sealed class NdpsAlertOutboxHandler(INotificationService notifications) : IOutboxHandler
{
    public string Type => NdpsService.AlertType;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var alert = message.Read<NdpsAlert>();
        return notifications.NotifyPermissionHoldersAsync(
            PharmacyPermissions.Registers, alert.FacilityId, alert.Title, alert.Body, alert.Link, "pharmacy", "critical", cancellationToken);
    }
}
