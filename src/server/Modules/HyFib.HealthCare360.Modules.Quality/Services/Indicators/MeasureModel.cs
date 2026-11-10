using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Services.Indicators;

/// <summary>Whether a record counts in the numerator, only in the denominator, or was left out (and why).</summary>
public enum MeasureRole
{
    Numerator = 0,
    Denominator,
    Excluded,
}

/// <summary>One record behind a computed value, for the drill-down.</summary>
/// <param name="Kind">What the record is: "Visit", "Admission", "Transfer out of ICU", "Device episode"…</param>
/// <param name="Reference">How staff find it: the visit, admission, incident, case or unit number.</param>
/// <param name="Minutes">For an average time, this record's minutes.</param>
/// <param name="Detail">Why it counts, or why it was left out.</param>
public sealed record MeasureRecord(MeasureRole Role, string Kind, string Reference, Guid? PatientId, DateTimeOffset At, decimal? Minutes, string? Detail);

/// <summary>A computed numerator and denominator with the records behind them.</summary>
/// <param name="Note">How the denominator was counted when it is not a list of records (in-patient days, device days, beds).</param>
/// <param name="Unavailable">The records it is computed from are not available in this installation (the theatre or blood bank is not there).</param>
public sealed record MeasureResult(decimal Numerator, decimal Denominator, IReadOnlyList<MeasureRecord> Records, string? Note = null, bool Unavailable = false);

/// <summary>A facility's reporting period, as local (IST) calendar days, both inclusive.</summary>
public sealed record MeasurePeriod(Guid FacilityId, DateOnly From, DateOnly To)
{
    public DateTimeOffset Start => QualityClock.StartOfDayUtc(From);

    /// <summary>The start of the day after the period.</summary>
    public DateTimeOffset End => QualityClock.StartOfDayUtc(To.AddDays(1));

    public int FromDay => From.DayNumber;

    public int ToDay => To.DayNumber;

    public int Days => To.DayNumber - From.DayNumber + 1;

    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;

    public static MeasurePeriod Month(Guid facilityId, DateOnly anyDay)
    {
        var first = new DateOnly(anyDay.Year, anyDay.Month, 1);
        return new MeasurePeriod(facilityId, first, first.AddMonths(1).AddDays(-1));
    }
}

/// <summary>A NABH indicator HC360 computes: its definition as seeded, and the computation it is measured by.</summary>
public sealed record MeasureDefinition(
    string Code,
    string Name,
    string Category,
    string Numerator,
    string Denominator,
    IndicatorUnit Unit,
    decimal? Target,
    IndicatorDirection Direction,
    string Nabh);
