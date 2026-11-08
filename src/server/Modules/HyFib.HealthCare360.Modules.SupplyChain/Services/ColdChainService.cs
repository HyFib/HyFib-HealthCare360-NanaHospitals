using System.Globalization;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.ColdChain;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>An alert about a cold-chain unit, delivered after the save to the store's in-charge (or whoever manages cold chain).</summary>
internal sealed record ColdChainAlert(Guid FacilityId, Guid? InChargeUserId, string Title, string Body, string Severity);

/// <summary>
/// The cold-chain temperature log (INV, PH-04): readings entered twice a day or sent by a unit's logger, judged against the
/// unit's range; an excursion alerts the store's in-charge with the cold-chain items stored there, and the daily register
/// shows each day's readings, the two checks taken or missed, and the excursions with what was done about them.
/// </summary>
public sealed class ColdChainService(HospitalDbContext db, IOutbox outbox, ICurrentUser current, IAuditLogger audit, TimeProvider time)
{
    internal const string AlertType = "supplychain.cold-chain-alert";

    /// <summary>A manual reading may be entered up to this long after it was taken (a night reading entered at handover).</summary>
    public static readonly TimeSpan ManualBackdateLimit = TimeSpan.FromHours(12);

    public const int MaxLoggerBatch = 500;

    // ------------------------------------------------------------------ units

    public async Task<Result<Guid>> SaveUnitAsync(Guid? id, SaveColdStorageUnitRequest request, CancellationToken ct)
    {
        var store = await db.Set<Store>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StoreId, ct);
        if (store is null || !store.IsActive)
        {
            return Error.Field("storeId", "Choose an active store of this facility.");
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<ColdStorageUnit>().AnyAsync(u => u.Code == code && u.Id != id, ct))
        {
            return Error.Conflict("cold_chain.duplicate_code", $"Unit code {code} is already used.");
        }

        ColdStorageUnit unit;
        string? rangeBefore = null;
        if (id is { } existing)
        {
            var found = await db.Set<ColdStorageUnit>().FirstOrDefaultAsync(u => u.Id == existing, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(ColdStorageUnit), existing);
            }

            unit = found;
            rangeBefore = unit.RangeWords();
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(unit, version);
            }
        }
        else
        {
            unit = new ColdStorageUnit { FacilityId = store.FacilityId };
            db.Add(unit);
        }

        var (defaultMin, defaultMax) = ColdStorageUnit.DefaultRange(request.Kind);
        unit.StoreId = store.Id;
        unit.Code = code;
        unit.Name = request.Name.Trim();
        unit.Kind = request.Kind;
        unit.MinCelsius = request.MinCelsius ?? defaultMin;
        unit.MaxCelsius = request.MaxCelsius ?? defaultMax;
        unit.IsActive = request.IsActive;
        var valid = unit.Validate();
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        // A wider range lets warmer (or colder) readings pass without an alert: who changed it, and from what, is kept.
        if (rangeBefore is not null && rangeBefore != unit.RangeWords())
        {
            audit.Add(new AuditEntry(AuditCategories.Admin, "inventory.cold_chain_range_changed",
                $"{unit.Code} {unit.Name}: allowed {unit.RangeWords()} (was {rangeBefore})")
            {
                EntityType = nameof(ColdStorageUnit),
                EntityId = unit.Id.ToString(),
                Severity = AuditSeverity.Warning,
            });
        }

        await db.SaveChangesAsync(ct);
        return unit.Id;
    }

    /// <summary>Issues a data logger key for the unit, shown once; any earlier key stops working.</summary>
    public async Task<Result<ColdChainLoggerKey>> IssueLoggerKeyAsync(Guid unitId, CancellationToken ct)
    {
        var unit = await db.Set<ColdStorageUnit>().FirstOrDefaultAsync(u => u.Id == unitId, ct);
        if (unit is null)
        {
            return Error.NotFound(nameof(ColdStorageUnit), unitId);
        }

        if (!unit.IsActive)
        {
            return Error.Rule("cold_chain.unit_inactive", $"{unit.Name} is not in use.");
        }

        var key = unit.IssueLoggerKey(time.GetUtcNow());
        audit.Add(new AuditEntry(AuditCategories.Admin, "inventory.cold_chain_logger_key", $"{unit.Code} {unit.Name}: a data logger key was issued")
        {
            EntityType = nameof(ColdStorageUnit),
            EntityId = unit.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return new ColdChainLoggerKey(unit.Id, key, ColdChainLoggerPath, ColdChainLoggerHeader);
    }

    public async Task<Result> RevokeLoggerKeyAsync(Guid unitId, CancellationToken ct)
    {
        var unit = await db.Set<ColdStorageUnit>().FirstOrDefaultAsync(u => u.Id == unitId, ct);
        if (unit is null)
        {
            return Error.NotFound(nameof(ColdStorageUnit), unitId);
        }

        unit.RevokeLoggerKey();
        audit.Add(new AuditEntry(AuditCategories.Admin, "inventory.cold_chain_logger_revoked", $"{unit.Code} {unit.Name}: the data logger key was revoked")
        {
            EntityType = nameof(ColdStorageUnit),
            EntityId = unit.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public const string ColdChainLoggerPath = "/api/v1/inventory/cold-chain/logger/readings";
    public const string ColdChainLoggerHeader = "X-Device-Key";

    // ------------------------------------------------------------------ readings

    /// <summary>A reading taken off the unit's thermometer by the signed-in user.</summary>
    public async Task<Result<TemperatureReadingRecorded>> RecordManualAsync(Guid unitId, RecordTemperatureRequest request, CancellationToken ct)
    {
        var unit = await db.Set<ColdStorageUnit>().FirstOrDefaultAsync(u => u.Id == unitId, ct);
        if (unit is null)
        {
            return Error.NotFound(nameof(ColdStorageUnit), unitId);
        }

        if (!unit.IsActive)
        {
            return Error.Rule("cold_chain.unit_inactive", $"{unit.Name} is not in use.");
        }

        var now = time.GetUtcNow();
        var takenAt = ToSecond(request.TakenAt ?? now);
        if (takenAt > now.AddMinutes(5))
        {
            return Error.Field("takenAt", "A reading can't be taken in the future.");
        }

        if (takenAt < now - ManualBackdateLimit)
        {
            return Error.Field("takenAt", "Enter a reading within 12 hours of taking it; an older one is a note in the register, not a reading.");
        }

        var reading = TemperatureReading.For(unit, takenAt, request.Celsius, TemperatureSource.Manual, now);
        reading.RecordedByUserId = current.UserId;
        reading.RecordedByName = current.DisplayName ?? current.UserName;
        reading.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var excursion = await RecordAsync(unit, reading, ct);
        await db.SaveChangesAsync(ct);
        return new TemperatureReadingRecorded(reading.Id, reading.Celsius, reading.OutOfRange, unit.RangeWords(), excursion?.Id ?? unit.OpenExcursionId);
    }

    /// <summary>Readings sent by a unit's data logger (its own key; anonymous at the HTTP level). Resent readings are skipped.</summary>
    public async Task<Result<LoggerUploadResult>> RecordFromLoggerAsync(ColdStorageUnit unit, IReadOnlyList<LoggerReadingInput> readings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(readings);
        var now = time.GetUtcNow();
        var rejected = new List<string>();
        var accepted = 0;
        var duplicates = 0;
        // A logger reports to the second; kept so, a batch resent after a lost reply matches what was stored (the database
        // keeps microseconds, a .NET time ticks of 100 ns).
        var inputs = readings.Select(r => r with { TakenAt = ToSecond(r.TakenAt) }).ToList();
        var times = inputs.Select(r => r.TakenAt).Distinct().ToList();
        var known = (await db.Set<TemperatureReading>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(r => r.UnitId == unit.Id && r.Source == TemperatureSource.Logger && times.Contains(r.TakenAt))
                .Select(r => r.TakenAt)
                .ToListAsync(ct))
            .ToHashSet();
        foreach (var input in inputs.OrderBy(r => r.TakenAt))
        {
            if (input.TakenAt > now.AddMinutes(5) || input.TakenAt < now.AddDays(-7))
            {
                rejected.Add($"{input.TakenAt:O}: a reading is taken within the last 7 days, and not in the future.");
                continue;
            }

            if (input.Celsius is < -100 or > 100)
            {
                rejected.Add($"{input.TakenAt:O}: {input.Celsius} °C is not a temperature a refrigerator or freezer reads.");
                continue;
            }

            if (!known.Add(input.TakenAt))
            {
                duplicates++;
                continue;
            }

            var reading = TemperatureReading.For(unit, input.TakenAt, input.Celsius, TemperatureSource.Logger, now);
            reading.RecordedByName = "Data logger";
            await RecordAsync(unit, reading, ct);
            accepted++;
        }

        await db.SaveChangesAsync(ct);
        return new LoggerUploadResult(accepted, duplicates, rejected);
    }

    /// <summary>Adds a reading, moves the unit's excursion, and queues the alert of an excursion that starts.</summary>
    private async Task<ColdChainExcursion?> RecordAsync(ColdStorageUnit unit, TemperatureReading reading, CancellationToken ct)
    {
        db.Add(reading);
        var open = unit.OpenExcursionId is { } openId
            ? db.Set<ColdChainExcursion>().Local.FirstOrDefault(e => e.Id == openId)
              ?? await db.Set<ColdChainExcursion>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(e => e.Id == openId, ct)
            : null;
        var started = unit.Record(reading, open);
        if (started is null)
        {
            return null;
        }

        var store = await db.Set<Store>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => s.Id == unit.StoreId).Select(s => new { s.Name, s.InChargeUserId }).FirstAsync(ct);
        var atRisk = await ItemsAtRiskAsync(unit.StoreId, ct);
        started.ItemsAtRisk = Fit(atRisk.Count == 0 ? "No cold-chain item with stock in the store." : string.Join("; ", atRisk), 2000);
        db.Add(started);
        var local = HospitalClock.ToLocalTime(reading.TakenAt);
        outbox.Enqueue(AlertType, new ColdChainAlert(unit.FacilityId, store.InChargeUserId,
            $"Temperature out of range: {unit.Name}, {store.Name}",
            Fit($"{ColdStorageUnit.Celsius(reading.Celsius)} at {local.ToString("dd-MMM HH:mm", CultureInfo.InvariantCulture)} ({reading.Source.ToString().ToLowerInvariant()}), "
                + $"allowed {unit.RangeWords()}. Cold-chain items stored there: {started.ItemsAtRisk} Check the unit, move the stock if needed, and record what was done.", 1000),
            "critical"), unit.FacilityId);
        audit.Add(new AuditEntry(AuditCategories.Clinical, "inventory.cold_chain_excursion",
            $"{unit.Code} {unit.Name}: {ColdStorageUnit.Celsius(reading.Celsius)}, allowed {unit.RangeWords()}")
        {
            EntityType = nameof(ColdStorageUnit),
            EntityId = unit.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        return started;
    }

    /// <summary>The cold-chain items with stock in a store, with their batches: "Insulin glargine (batch G12, 10 PEN)".</summary>
    internal async Task<List<string>> ItemsAtRiskAsync(Guid storeId, CancellationToken ct)
    {
        var rows = await (
                from b in db.Set<StockBatch>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                join i in db.Set<Item>() on b.ItemId equals i.Id
                where b.StoreId == storeId && b.QuantityOnHand > 0 && i.IsColdChain
                orderby i.Name, b.ExpiryDate
                select new { i.Name, i.BaseUnit, b.BatchNumber, b.QuantityOnHand })
            .Take(40)
            .ToListAsync(ct);
        return rows.Select(r => $"{r.Name} (batch {r.BatchNumber}, {StockErrors.Format(r.QuantityOnHand)} {r.BaseUnit})").ToList();
    }

    /// <summary>Records what was done about the stock during an excursion (moved, quarantined, manufacturer consulted).</summary>
    public async Task<Result> RecordActionAsync(Guid excursionId, ExcursionActionRequest request, CancellationToken ct)
    {
        var excursion = await db.Set<ColdChainExcursion>().FirstOrDefaultAsync(e => e.Id == excursionId, ct);
        if (excursion is null)
        {
            return Error.NotFound(nameof(ColdChainExcursion), excursionId);
        }

        var recorded = excursion.RecordAction(request.Action, current.UserId, current.DisplayName ?? current.UserName ?? "Unknown", time.GetUtcNow());
        if (recorded.IsFailure)
        {
            return recorded;
        }

        audit.Add(new AuditEntry(AuditCategories.Clinical, "inventory.cold_chain_excursion_action", Fit($"Excursion {excursion.ExtremeWords()}: {excursion.ActionTaken}", 1000))
        {
            EntityType = nameof(ColdChainExcursion),
            EntityId = excursion.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ------------------------------------------------------------------ views

    public async Task<IReadOnlyList<ColdStorageUnitDto>> UnitsAsync(Guid? storeId, CancellationToken ct)
    {
        var units = db.Set<ColdStorageUnit>().AsNoTracking();
        if (storeId is { } store)
        {
            units = units.Where(u => u.StoreId == store);
        }

        var rows = await (from u in units
                          join s in db.Set<Store>() on u.StoreId equals s.Id
                          orderby s.Name, u.Name
                          select new { Unit = u, StoreName = s.Name }).ToListAsync(ct);
        var today = HospitalClock.Today(time);
        var (start, end) = HospitalClock.Range(today, today);
        var ids = rows.Select(r => r.Unit.Id).ToList();
        var manualToday = await db.Set<TemperatureReading>().AsNoTracking()
            .Where(r => ids.Contains(r.UnitId) && r.Source == TemperatureSource.Manual && r.TakenAt >= start && r.TakenAt < end)
            .Select(r => new { r.UnitId, r.TakenAt })
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        return rows.Select(r =>
        {
            var u = r.Unit;
            var slots = Slots(today, manualToday.Where(m => m.UnitId == u.Id).Select(m => m.TakenAt).ToList(), now);
            return new ColdStorageUnitDto(
                u.Id, u.StoreId, r.StoreName, u.Code, u.Name, u.Kind, u.MinCelsius, u.MaxCelsius, u.IsActive, u.HasLogger, u.LoggerKeyIssuedAt,
                u.LastReadingAt, u.LastCelsius, u.LastCelsius is { } last && !u.InRange(last), u.OpenExcursionId, u.LastLoggerReadingAt,
                slots.Find(s => s.Slot == ColdChainSlot.Morning)!.Status, slots.Find(s => s.Slot == ColdChainSlot.Evening)!.Status, u.Version);
        }).ToList();
    }

    /// <summary>The day's register for a unit: every reading, the two checks, the low and high, and the excursions that day.</summary>
    public async Task<Result<ColdChainDailyRegister>> RegisterAsync(Guid unitId, DateOnly date, CancellationToken ct)
    {
        var unit = await db.Set<ColdStorageUnit>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, ct);
        if (unit is null)
        {
            return Error.NotFound(nameof(ColdStorageUnit), unitId);
        }

        var store = await db.Set<Store>().AsNoTracking().Where(s => s.Id == unit.StoreId).Select(s => s.Name).FirstAsync(ct);
        var (start, end) = HospitalClock.Range(date, date);
        var readings = await db.Set<TemperatureReading>().AsNoTracking()
            .Where(r => r.UnitId == unitId && r.TakenAt >= start && r.TakenAt < end)
            .OrderBy(r => r.TakenAt)
            .ThenBy(r => r.RecordedAt)
            .ToListAsync(ct);
        var excursions = await db.Set<ColdChainExcursion>().AsNoTracking()
            .Where(e => e.UnitId == unitId && e.StartedAt < end && (e.EndedAt == null || e.EndedAt >= start))
            .OrderBy(e => e.StartedAt)
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        var slots = Slots(date, readings.Where(r => r.Source == TemperatureSource.Manual).Select(r => r.TakenAt).ToList(), now);
        foreach (var slot in slots)
        {
            var taken = slot.TakenAt is { } at ? readings.FirstOrDefault(r => r.Source == TemperatureSource.Manual && r.TakenAt == at) : null;
            if (taken is not null)
            {
                slot.Celsius = taken.Celsius;
                slot.RecordedBy = taken.RecordedByName;
            }
        }

        return new ColdChainDailyRegister(
            unit.Id, unit.Code, unit.Name, store, unit.Kind, date, unit.MinCelsius, unit.MaxCelsius,
            readings.Count == 0 ? null : readings.Min(r => r.Celsius),
            readings.Count == 0 ? null : readings.Max(r => r.Celsius),
            slots.Select(s => new ColdChainCheck(s.Slot, s.Status, s.TakenAt, s.Celsius, s.RecordedBy)).ToList(),
            readings.Select(r => new TemperatureReadingDto(r.Id, r.TakenAt, r.Celsius, r.Source, r.OutOfRange, r.MinCelsius, r.MaxCelsius, r.RecordedByName, r.Note)).ToList(),
            excursions.Select(ToDto).ToList());
    }

    public async Task<IReadOnlyList<ColdChainExcursionDto>> ExcursionsAsync(Guid? storeId, bool openOnly, CancellationToken ct)
    {
        var excursions = db.Set<ColdChainExcursion>().AsNoTracking();
        if (openOnly)
        {
            excursions = excursions.Where(e => e.EndedAt == null || e.ActionTaken == null);
        }

        if (storeId is { } store)
        {
            var units = db.Set<ColdStorageUnit>().Where(u => u.StoreId == store).Select(u => u.Id);
            excursions = excursions.Where(e => units.Contains(e.UnitId));
        }

        return (await excursions.OrderByDescending(e => e.StartedAt).Take(200).ToListAsync(ct)).Select(ToDto).ToList();
    }

    private static ColdChainExcursionDto ToDto(ColdChainExcursion e) => new(
        e.Id, e.UnitId, e.StartedAt, e.EndedAt, e.LowestCelsius, e.HighestCelsius, e.ReadingsOutOfRange, e.MinCelsius, e.MaxCelsius,
        e.ExtremeWords(), e.ItemsAtRisk, e.ActionTaken, e.ActionByName, e.ActionAt);

    private sealed class SlotState(ColdChainSlot slot)
    {
        public ColdChainSlot Slot { get; } = slot;
        public ColdChainCheckStatus Status { get; set; }
        public DateTimeOffset? TakenAt { get; set; }
        public decimal? Celsius { get; set; }
        public string? RecordedBy { get; set; }
    }

    private List<SlotState> Slots(DateOnly date, IReadOnlyList<DateTimeOffset> manual, DateTimeOffset now) =>
        Enum.GetValues<ColdChainSlot>().Select(slot =>
        {
            var (from, until) = ColdChainSchedule.WindowUtc(date, slot);
            var taken = manual.Where(t => t >= from && t < until).Order().Cast<DateTimeOffset?>().FirstOrDefault();
            return new SlotState(slot)
            {
                TakenAt = taken,
                Status = taken is not null ? ColdChainCheckStatus.Taken : now >= until ? ColdChainCheckStatus.Missed : now >= from ? ColdChainCheckStatus.Due : ColdChainCheckStatus.Upcoming,
            };
        }).ToList();

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    // ------------------------------------------------------------------ the job

    /// <summary>
    /// Alerts each active unit's twice-daily checks missed since the last run, once each, and loggers silent for
    /// <see cref="ColdChainSchedule.LoggerSilence"/>; run by <see cref="ColdChainJob"/> across facilities.
    /// </summary>
    internal async Task<int> AlertMissedAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var units = await db.Set<ColdStorageUnit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(u => u.IsActive).ToListAsync(ct);
        var alerts = 0;
        foreach (var unit in units)
        {
            var store = await db.Set<Store>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(s => s.Id == unit.StoreId).Select(s => new { s.Name, s.InChargeUserId }).FirstAsync(ct);

            // From when the unit was set up, or the last check alerted; never further back than two days.
            var after = Max(unit.MissedAlertedThrough ?? unit.CreatedAt, now.AddDays(-2));
            var missed = new List<string>();
            DateTimeOffset? through = null;
            foreach (var (date, slot, from, until) in ColdChainSchedule.EndedBetween(after, now))
            {
                through = until;
                // A unit set up during a check's window is not held to that check.
                if (from < unit.CreatedAt)
                {
                    continue;
                }

                var taken = await db.Set<TemperatureReading>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                    .AnyAsync(r => r.UnitId == unit.Id && r.Source == TemperatureSource.Manual && r.TakenAt >= from && r.TakenAt < until, ct);
                if (!taken)
                {
                    missed.Add($"{slot.ToString().ToLowerInvariant()} of {date.ToString("dd-MMM", CultureInfo.InvariantCulture)}");
                }
            }

            if (through is not null)
            {
                unit.MissedAlertedThrough = through;
            }

            if (missed.Count > 0)
            {
                outbox.Enqueue(AlertType, new ColdChainAlert(unit.FacilityId, store.InChargeUserId,
                    $"Temperature not recorded: {unit.Name}, {store.Name}",
                    $"The {string.Join(" and ", missed)} temperature check was not recorded. Read the thermometer and enter it in the cold-chain register.",
                    "warning"), unit.FacilityId);
                alerts++;
            }

            if (unit.HasLogger && unit.LoggerSilenceAlertedAt is null
                && now - (unit.LastLoggerReadingAt ?? unit.LoggerKeyIssuedAt ?? now) > ColdChainSchedule.LoggerSilence)
            {
                unit.LoggerSilenceAlertedAt = now;
                outbox.Enqueue(AlertType, new ColdChainAlert(unit.FacilityId, store.InChargeUserId,
                    $"Data logger silent: {unit.Name}, {store.Name}",
                    $"No reading from the unit's data logger for over {ColdChainSchedule.LoggerSilence.TotalHours:0} hours. Check the logger, its battery and network, and read the thermometer by hand meanwhile.",
                    "warning"), unit.FacilityId);
                alerts++;
            }
        }

        await db.SaveChangesAsync(ct);
        return alerts;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    internal static DateTimeOffset ToSecond(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }
}

/// <summary>Delivers a cold-chain alert: to the store's in-charge, or with none set, to everyone who manages cold chain in the facility.</summary>
internal sealed class ColdChainAlertOutboxHandler(INotificationService notifications) : IOutboxHandler
{
    public string Type => ColdChainService.AlertType;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var alert = message.Read<ColdChainAlert>();
        return alert.InChargeUserId is { } inCharge
            ? notifications.NotifyUsersAsync([inCharge], alert.Title, alert.Body, "/inventory/cold-chain", "inventory", alert.Severity, cancellationToken)
            : notifications.NotifyPermissionHoldersAsync(SupplyChainPermissions.ColdChainManage, alert.FacilityId, alert.Title, alert.Body,
                "/inventory/cold-chain", "inventory", alert.Severity, cancellationToken);
    }
}
