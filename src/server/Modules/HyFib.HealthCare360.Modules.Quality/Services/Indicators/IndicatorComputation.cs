using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Services.Indicators;

/// <summary>What happened to one indicator in a computation run.</summary>
public enum ComputeOutcome
{
    Stored = 0,

    /// <summary>The period has a value the quality team entered by hand: it is kept, not overwritten.</summary>
    KeptHandEntry,

    /// <summary>Nothing to measure in the period (no denominator): no value is stored.</summary>
    NothingToMeasure,

    /// <summary>The records it is computed from are not in this installation.</summary>
    SourceUnavailable,
}

public sealed record ComputedIndicator(Guid IndicatorId, string Code, string Name, DateOnly PeriodStart, ComputeOutcome Outcome, decimal? Numerator, decimal? Denominator,
    decimal? Value, bool? MeetsTarget);

/// <summary>
/// Computes the facility's computed indicators for a period and stores their values (numerator, denominator, value,
/// target met, when computed). A value the quality team entered by hand for that period is never overwritten. Computing a
/// period again replaces the computed values, so late documentation is taken in.
/// </summary>
public sealed class IndicatorComputation(HospitalDbContext db, IndicatorMeasures measures, TimeProvider time)
{
    public async Task<IReadOnlyList<ComputedIndicator>> ComputeAsync(Guid facilityId, DateOnly anyDayInPeriod, Guid? onlyIndicatorId, CancellationToken ct)
    {
        var indicators = await db.Set<QualityIndicator>()
            .Where(i => i.IsActive && i.ComputedBy != null && (onlyIndicatorId == null || i.Id == onlyIndicatorId))
            .OrderBy(i => i.SortOrder)
            .ToListAsync(ct);
        var today = time.LocalToday();
        var now = time.GetUtcNow();
        var results = new List<ComputedIndicator>();
        foreach (var indicator in indicators)
        {
            var period = PeriodOf(facilityId, anyDayInPeriod, indicator.Frequency, today);
            var measured = await measures.ComputeAsync(indicator.ComputedBy!, period, ct);
            var existing = await db.Set<IndicatorValue>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .FirstOrDefaultAsync(v => v.FacilityId == facilityId && v.IndicatorId == indicator.Id && v.PeriodStart == period.From, ct);

            ComputeOutcome outcome;
            decimal? value = null;
            bool? meets = null;
            if (measured.Unavailable)
            {
                outcome = ComputeOutcome.SourceUnavailable;
            }
            else if (existing is { IsComputed: false })
            {
                outcome = ComputeOutcome.KeptHandEntry;
            }
            else if (IndicatorMath.Compute(measured.Numerator, measured.Denominator, indicator.Multiplier) is not { } computed)
            {
                outcome = ComputeOutcome.NothingToMeasure;
                if (existing is not null)
                {
                    db.Remove(existing);
                }
            }
            else
            {
                outcome = ComputeOutcome.Stored;
                value = computed;
                meets = IndicatorMath.MeetsTarget(computed, indicator.Target, indicator.Direction);
                if (existing is null)
                {
                    existing = new IndicatorValue { FacilityId = facilityId, IndicatorId = indicator.Id, PeriodStart = period.From };
                    db.Add(existing);
                }

                existing.Numerator = measured.Numerator;
                existing.Denominator = measured.Denominator;
                existing.Value = computed;
                existing.MeetsTarget = meets;
                existing.IsComputed = true;
                existing.ComputedAt = now;
                existing.Notes = Notes(measured, period, today);
            }

            results.Add(new ComputedIndicator(indicator.Id, indicator.Code, indicator.Name, period.From, outcome,
                measured.Unavailable ? null : measured.Numerator, measured.Unavailable ? null : measured.Denominator, value, meets));
        }

        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>The indicator's reporting period containing the day, cut at today for a period still running.</summary>
    public static MeasurePeriod PeriodOf(Guid facilityId, DateOnly anyDay, IndicatorFrequency frequency, DateOnly today)
    {
        var start = IndicatorMath.PeriodStart(anyDay, frequency);
        var end = frequency switch
        {
            IndicatorFrequency.Monthly => start.AddMonths(1),
            IndicatorFrequency.Quarterly => start.AddMonths(3),
            _ => start.AddYears(1),
        };
        var last = end.AddDays(-1);
        return new MeasurePeriod(facilityId, start, last > today ? today : last);
    }

    private static string? Notes(MeasureResult measured, MeasurePeriod period, DateOnly today)
    {
        var partial = period.To == today ? $"Computed to {today:dd MMM yyyy}: the period is still running. " : string.Empty;
        var text = partial + (measured.Note ?? string.Empty);
        return text.Length == 0 ? null : text.Length <= 1000 ? text.Trim() : text[..999] + "…";
    }
}
