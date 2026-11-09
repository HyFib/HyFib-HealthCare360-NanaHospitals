using System.Globalization;

namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum ColdUnitKind
{
    /// <summary>A pharmaceutical refrigerator (ice-lined or not): 2–8 °C.</summary>
    Refrigerator = 0,

    /// <summary>A deep freezer: −25 to −15 °C.</summary>
    Freezer,

    /// <summary>A walk-in cold room: 2–8 °C.</summary>
    ColdRoom,
}

public enum TemperatureSource
{
    /// <summary>Read off the unit's thermometer and entered by a member of staff.</summary>
    Manual = 0,

    /// <summary>Sent by the unit's data logger with its own key.</summary>
    Logger,
}

/// <summary>The twice-daily manual check: morning and evening.</summary>
public enum ColdChainSlot
{
    Morning = 0,
    Evening,
}

/// <summary>
/// A refrigerator, freezer or cold room of a store, where cold-chain items (insulin, vaccines, some antibiotics and
/// biologicals) are kept, with the range its readings must stay in. Its temperature is read twice a day by hand (the
/// register) and, where it has one, sent by a data logger. A reading outside the range opens an excursion and alerts the
/// store's in-charge with the cold-chain items stored there; the excursion closes at the next reading back in range.
/// </summary>
public sealed class ColdStorageUnit : AuditableEntity, IFacilityScoped
{
    public const decimal FridgeMin = 2m;
    public const decimal FridgeMax = 8m;
    public const decimal FreezerMin = -25m;
    public const decimal FreezerMax = -15m;

    public Guid FacilityId { get; set; }
    public Guid StoreId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ColdUnitKind Kind { get; set; }

    /// <summary>The lowest allowed reading, °C.</summary>
    public decimal MinCelsius { get; set; } = FridgeMin;

    /// <summary>The highest allowed reading, °C.</summary>
    public decimal MaxCelsius { get; set; } = FridgeMax;

    public bool IsActive { get; set; } = true;

    /// <summary>SHA-256 of the data logger's key (<see cref="ColdChainLoggerKeys"/>); null when the unit has no logger.</summary>
    public string? LoggerKeyHash { get; private set; }

    public DateTimeOffset? LoggerKeyIssuedAt { get; private set; }

    public DateTimeOffset? LastReadingAt { get; private set; }
    public decimal? LastCelsius { get; private set; }
    public DateTimeOffset? LastLoggerReadingAt { get; private set; }

    /// <summary>The excursion open now (a reading out of range not yet followed by one back in range).</summary>
    public Guid? OpenExcursionId { get; private set; }

    /// <summary>The due time of the last twice-daily check whose miss was alerted, so each miss is alerted once.</summary>
    public DateTimeOffset? MissedAlertedThrough { get; set; }

    /// <summary>When a silent logger was last alerted, so it is alerted once until it is heard from again.</summary>
    public DateTimeOffset? LoggerSilenceAlertedAt { get; set; }

    public bool HasLogger => LoggerKeyHash is not null;

    public static (decimal Min, decimal Max) DefaultRange(ColdUnitKind kind) =>
        kind == ColdUnitKind.Freezer ? (FreezerMin, FreezerMax) : (FridgeMin, FridgeMax);

    public bool InRange(decimal celsius) => celsius >= MinCelsius && celsius <= MaxCelsius;

    public string RangeWords() => $"{Celsius(MinCelsius)} to {Celsius(MaxCelsius)}";

    public Result Validate()
    {
        if (MinCelsius >= MaxCelsius)
        {
            return Error.Field("minCelsius", "The lowest allowed temperature must be below the highest.");
        }

        return MinCelsius < -90 || MaxCelsius > 30
            ? Error.Field("maxCelsius", "Allowed temperatures are between −90 °C and 30 °C.")
            : Result.Success();
    }

    /// <summary>Issues a new logger key (the old one stops working); the key is returned once and only its hash is kept.</summary>
    public string IssueLoggerKey(DateTimeOffset now)
    {
        var key = ColdChainLoggerKeys.Create(Id);
        LoggerKeyHash = ColdChainLoggerKeys.Hash(key);
        LoggerKeyIssuedAt = now;
        LoggerSilenceAlertedAt = null;
        return key;
    }

    public void RevokeLoggerKey()
    {
        LoggerKeyHash = null;
        LoggerKeyIssuedAt = null;
    }

    public bool AcceptsLogger(string? key) => IsActive && ColdChainLoggerKeys.Verify(key, LoggerKeyHash);

    /// <summary>
    /// Records a reading against the unit and its excursion: one out of range with none open opens one (returned, to be
    /// alerted); one out of range while one is open extends it; one back in range closes it. Readings arrive out of order
    /// from a logger that was offline: one older than the last reading is kept in the register but moves nothing.
    /// </summary>
    public ColdChainExcursion? Record(TemperatureReading reading, ColdChainExcursion? open)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Source == TemperatureSource.Logger && (LastLoggerReadingAt is null || reading.TakenAt > LastLoggerReadingAt))
        {
            LastLoggerReadingAt = reading.TakenAt;
            LoggerSilenceAlertedAt = null;
        }

        if (LastReadingAt is { } last && reading.TakenAt < last)
        {
            if (open is not null && reading.OutOfRange && reading.TakenAt >= open.StartedAt)
            {
                open.Include(reading);
            }

            return null;
        }

        LastReadingAt = reading.TakenAt;
        LastCelsius = reading.Celsius;
        if (reading.OutOfRange)
        {
            if (open is not null)
            {
                open.Include(reading);
                return null;
            }

            var excursion = ColdChainExcursion.Start(this, reading);
            OpenExcursionId = excursion.Id;
            return excursion;
        }

        if (open is not null)
        {
            open.End(reading.TakenAt);
            OpenExcursionId = null;
        }

        return null;
    }

    internal static string Celsius(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture) + " °C";
}

/// <summary>One temperature reading of a unit, manual or from its logger, with the range it was judged against.</summary>
public sealed class TemperatureReading : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid UnitId { get; set; }
    public DateTimeOffset TakenAt { get; set; }
    public decimal Celsius { get; set; }
    public TemperatureSource Source { get; set; }

    /// <summary>The range in force when it was taken (a range changed later does not re-judge old readings).</summary>
    public decimal MinCelsius { get; set; }

    public decimal MaxCelsius { get; set; }
    public bool OutOfRange { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? RecordedByName { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    public static TemperatureReading For(ColdStorageUnit unit, DateTimeOffset takenAt, decimal celsius, TemperatureSource source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return new TemperatureReading
        {
            FacilityId = unit.FacilityId,
            UnitId = unit.Id,
            TakenAt = takenAt,
            Celsius = Math.Round(celsius, 1, MidpointRounding.AwayFromZero),
            Source = source,
            MinCelsius = unit.MinCelsius,
            MaxCelsius = unit.MaxCelsius,
            OutOfRange = !unit.InRange(Math.Round(celsius, 1, MidpointRounding.AwayFromZero)),
            RecordedAt = now,
        };
    }
}

/// <summary>
/// A temperature excursion: from the first reading out of range to the first back in range, with the extreme seen and
/// what the store did about the stock (moved it, quarantined it, asked the manufacturer), as the cold-chain register keeps.
/// </summary>
[Audited]
public sealed class ColdChainExcursion : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid UnitId { get; set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public decimal LowestCelsius { get; private set; }
    public decimal HighestCelsius { get; private set; }
    public int ReadingsOutOfRange { get; private set; }
    public decimal MinCelsius { get; private set; }
    public decimal MaxCelsius { get; private set; }

    /// <summary>The cold-chain items with stock in the store when it started, as alerted ("Insulin glargine 100 IU/ml: 12 PEN").</summary>
    public string? ItemsAtRisk { get; set; }

    public string? ActionTaken { get; private set; }
    public Guid? ActionByUserId { get; private set; }
    public string? ActionByName { get; private set; }
    public DateTimeOffset? ActionAt { get; private set; }

    public bool IsOpen => EndedAt is null;

    internal static ColdChainExcursion Start(ColdStorageUnit unit, TemperatureReading reading) => new()
    {
        FacilityId = unit.FacilityId,
        UnitId = unit.Id,
        StartedAt = reading.TakenAt,
        LowestCelsius = reading.Celsius,
        HighestCelsius = reading.Celsius,
        ReadingsOutOfRange = 1,
        MinCelsius = reading.MinCelsius,
        MaxCelsius = reading.MaxCelsius,
    };

    internal void Include(TemperatureReading reading)
    {
        LowestCelsius = Math.Min(LowestCelsius, reading.Celsius);
        HighestCelsius = Math.Max(HighestCelsius, reading.Celsius);
        ReadingsOutOfRange++;
    }

    internal void End(DateTimeOffset at) => EndedAt = at;

    /// <summary>Records what was done about the stock; it may be added to while the excursion lasts, and replaced by a later entry.</summary>
    public Result RecordAction(string action, Guid? userId, string userName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return Error.Field("action", "Say what was done about the stock.");
        }

        ActionTaken = action.Trim();
        ActionByUserId = userId;
        ActionByName = userName;
        ActionAt = now;
        return Result.Success();
    }

    /// <summary>The extreme in words: "up to 11.2 °C" for too warm, "down to −1 °C" for too cold (freezing is as harmful).</summary>
    public string ExtremeWords() =>
        HighestCelsius > MaxCelsius && LowestCelsius < MinCelsius
            ? $"between {ColdStorageUnit.Celsius(LowestCelsius)} and {ColdStorageUnit.Celsius(HighestCelsius)}"
            : HighestCelsius > MaxCelsius ? $"up to {ColdStorageUnit.Celsius(HighestCelsius)}" : $"down to {ColdStorageUnit.Celsius(LowestCelsius)}";
}

/// <summary>
/// The twice-daily manual check (the cold-chain register): a morning reading between 06:00 and 11:00 and an evening reading
/// between 16:00 and 21:00, hospital time. A data logger does not stand in for it: the thermometer is read and entered by
/// hand twice a day whether or not a logger is fitted (it is how a failed logger is noticed). A check not taken by the end
/// of its window is missed, and alerted once. A logger not heard from for <see cref="LoggerSilence"/> is alerted too.
/// </summary>
public static class ColdChainSchedule
{
    public static readonly TimeSpan LoggerSilence = TimeSpan.FromHours(2);

    public static (TimeOnly From, TimeOnly Until) Window(ColdChainSlot slot) =>
        slot == ColdChainSlot.Morning ? (new TimeOnly(6, 0), new TimeOnly(11, 0)) : (new TimeOnly(16, 0), new TimeOnly(21, 0));

    /// <summary>The slot a manual reading taken at local time <paramref name="local"/> counts for, if any.</summary>
    public static ColdChainSlot? SlotOf(TimeOnly local)
    {
        foreach (var slot in Enum.GetValues<ColdChainSlot>())
        {
            var (from, until) = Window(slot);
            if (local >= from && local < until)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>The UTC start and end of a slot's window on a local date.</summary>
    public static (DateTimeOffset From, DateTimeOffset Until) WindowUtc(DateOnly date, ColdChainSlot slot)
    {
        var (from, until) = Window(slot);
        var day = HospitalClock.StartOfDayUtc(date);
        return (day + from.ToTimeSpan(), day + until.ToTimeSpan());
    }

    /// <summary>The windows that ended after <paramref name="after"/> and at or before <paramref name="now"/>, oldest first.</summary>
    public static IEnumerable<(DateOnly Date, ColdChainSlot Slot, DateTimeOffset From, DateTimeOffset Until)> EndedBetween(DateTimeOffset after, DateTimeOffset now)
    {
        var first = HospitalClock.ToLocalDate(after);
        var last = HospitalClock.ToLocalDate(now);
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            foreach (var slot in Enum.GetValues<ColdChainSlot>())
            {
                var (from, until) = WindowUtc(date, slot);
                if (until > after && until <= now)
                {
                    yield return (date, slot, from, until);
                }
            }
        }
    }
}

/// <summary>
/// Data logger keys: <c>ccl_{unitId:N}.{secret}</c>, sent in the <c>X-Device-Key</c> header as the patient monitors' are.
/// The unit id lets the server find the unit without searching by secret; the whole key is hashed (SHA-256) and compared in
/// constant time.
/// </summary>
public static class ColdChainLoggerKeys
{
    public const string Prefix = "ccl_";
    public const int MaxLength = 128;

    public static string Create(Guid unitId) => $"{Prefix}{unitId:N}.{SecretTokens.NewOpaqueToken(32)}";

    public static string Hash(string key) => SecretTokens.Hash(key);

    public static bool TryGetUnitId(string? key, out Guid unitId)
    {
        unitId = Guid.Empty;
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot == Prefix.Length + 32 && dot < key.Length - 1 && Guid.TryParseExact(key.AsSpan(Prefix.Length, 32), "N", out unitId);
    }

    public static bool Verify(string? key, string? storedHash) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxLength && !string.IsNullOrEmpty(storedHash) && SecretTokens.FixedTimeEquals(storedHash, Hash(key));
}
