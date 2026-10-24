using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;

namespace HyFib.HealthCare360.Modules.Quality.Features.Indicators;

/// <summary>/api/v1/quality/indicators — NABH quality indicators: definitions, periodic values, trend and scorecard (IRMS-06).</summary>
internal static class IndicatorEndpoints
{
    private const int DefaultTrendPeriods = 12;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/indicators").WithTags("Quality").RequirePermission(QualityPermissions.IndicatorsManage);

        group.MapGet("/", List);
        group.MapGet("/scorecard", Scorecard);
        group.MapPost("/", (SaveIndicatorRequest r, HospitalDbContext db, CancellationToken ct) => Save(null, r, db, ct)).WithValidation<SaveIndicatorRequest>();
        group.MapPut("/{id:guid}", (Guid id, SaveIndicatorRequest r, HospitalDbContext db, CancellationToken ct) => Save(id, r, db, ct)).WithValidation<SaveIndicatorRequest>().RequireVersion();
        group.MapGet("/{id:guid}/values", Values);
        group.MapPut("/{id:guid}/values", RecordValue).WithValidation<RecordIndicatorValueRequest>().FirstSaveCreates();
        group.MapDelete("/values/{valueId:guid}", DeleteValue);
        group.MapGet("/{id:guid}/trend", Trend);
    }

    private static async Task<IResult> List(string? category, bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var indicators = await db.Set<QualityIndicator>().AsNoTracking()
            .Where(i => (includeInactive == true || i.IsActive) && (category == null || i.Category == category))
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Code)
            .ToListAsync(ct);
        var ids = indicators.Select(i => i.Id).ToList();
        var latest = (await db.Set<IndicatorValue>().AsNoTracking()
                .Where(v => ids.Contains(v.IndicatorId))
                .GroupBy(v => v.IndicatorId)
                .Select(g => g.OrderByDescending(v => v.PeriodStart).First())
                .ToListAsync(ct))
            .ToDictionary(v => v.IndicatorId);

        return TypedResults.Ok(indicators.Select(i => new IndicatorDto(
            i.Id, i.Code, i.Name, i.Category, i.NumeratorDefinition, i.DenominatorDefinition, i.Unit, i.Multiplier, i.Target, i.Direction, i.Frequency,
            i.NabhReference, i.SortOrder, i.IsActive,
            latest.TryGetValue(i.Id, out var v) ? new IndicatorLatest(v.PeriodStart, IndicatorMath.PeriodLabel(v.PeriodStart, i.Frequency), v.Value, v.MeetsTarget) : null,
            i.Version)).ToList());
    }

    private static async Task<IResult> Save(Guid? id, SaveIndicatorRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<QualityIndicator>().AnyAsync(i => i.Code == code && i.Id != id, ct))
        {
            return Error.Conflict("indicator.code_in_use", $"Indicator {code} already exists.").ToProblem();
        }

        QualityIndicator indicator;
        if (id is { } existingId)
        {
            var found = await db.Set<QualityIndicator>().FirstOrDefaultAsync(i => i.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(QualityIndicator), existingId).ToProblem();
            }

            indicator = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(indicator, version);
            }
        }
        else
        {
            indicator = new QualityIndicator();
            db.Add(indicator);
        }

        indicator.Code = code;
        indicator.Name = request.Name.Trim();
        indicator.Category = request.Category.Trim();
        indicator.NumeratorDefinition = request.NumeratorDefinition.Trim();
        indicator.DenominatorDefinition = request.DenominatorDefinition.Trim();
        indicator.Unit = request.Unit;
        indicator.Multiplier = request.Multiplier ?? IndicatorMath.DefaultMultiplier(request.Unit);
        indicator.Target = request.Target;
        indicator.Direction = request.Direction;
        indicator.Frequency = request.Frequency;
        indicator.NabhReference = string.IsNullOrWhiteSpace(request.NabhReference) ? null : request.NabhReference.Trim();
        indicator.SortOrder = request.SortOrder;
        indicator.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/quality/indicators/{indicator.Id}", new { indicator.Id }) : TypedResults.NoContent();
    }

    private static async Task<IResult> Values(Guid id, DateOnly? from, DateOnly? to, HospitalDbContext db, CancellationToken ct)
    {
        var indicator = await db.Set<QualityIndicator>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (indicator is null)
        {
            return Error.NotFound(nameof(QualityIndicator), id).ToProblem();
        }

        var values = await db.Set<IndicatorValue>().AsNoTracking()
            .Where(v => v.IndicatorId == id && (from == null || v.PeriodStart >= from) && (to == null || v.PeriodStart <= to))
            .OrderByDescending(v => v.PeriodStart)
            .Take(120)
            .ToListAsync(ct);
        return TypedResults.Ok(values.Select(v => ToDto(v, indicator.Frequency)).ToList());
    }

    /// <summary>Upserts the value for the period containing the given date; target status is evaluated against the current target.</summary>
    private static async Task<IResult> RecordValue(Guid id, RecordIndicatorValueRequest request, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var indicator = await db.Set<QualityIndicator>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.IsActive, ct);
        if (indicator is null)
        {
            return Error.NotFound(nameof(QualityIndicator), id).ToProblem();
        }

        var periodStart = IndicatorMath.PeriodStart(request.PeriodStart, indicator.Frequency);
        if (periodStart > time.LocalToday())
        {
            return Error.Field("periodStart", "Values can be recorded only for current or past periods.").ToProblem();
        }

        var computed = IndicatorMath.Compute(request.Numerator, request.Denominator, indicator.Multiplier);
        if (computed is not { } value)
        {
            return Error.Field("denominator", "The denominator must be more than zero.").ToProblem();
        }

        var existing = await db.Set<IndicatorValue>().FirstOrDefaultAsync(v => v.IndicatorId == id && v.PeriodStart == periodStart, ct);
        if (existing is null)
        {
            existing = new IndicatorValue { FacilityId = facilityId, IndicatorId = id, PeriodStart = periodStart };
            db.Add(existing);
        }

        db.SetExpectedVersion(existing, request.Version);

        existing.Numerator = request.Numerator;
        existing.Denominator = request.Denominator;
        existing.Value = value;
        existing.MeetsTarget = IndicatorMath.MeetsTarget(value, indicator.Target, indicator.Direction);
        existing.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(existing, indicator.Frequency));
    }

    private static async Task<IResult> DeleteValue(Guid valueId, HospitalDbContext db, CancellationToken ct)
    {
        var value = await db.Set<IndicatorValue>().FirstOrDefaultAsync(v => v.Id == valueId, ct);
        if (value is null)
        {
            return Error.NotFound(nameof(IndicatorValue), valueId).ToProblem();
        }

        db.Remove(value);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Trend(Guid id, DateOnly? from, DateOnly? to, HospitalDbContext db, CancellationToken ct)
    {
        var indicator = await db.Set<QualityIndicator>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (indicator is null)
        {
            return Error.NotFound(nameof(QualityIndicator), id).ToProblem();
        }

        var query = db.Set<IndicatorValue>().AsNoTracking().Where(v => v.IndicatorId == id && (from == null || v.PeriodStart >= from) && (to == null || v.PeriodStart <= to));
        var values = (await query.OrderByDescending(v => v.PeriodStart).Take(from is null ? DefaultTrendPeriods : 120).ToListAsync(ct))
            .OrderBy(v => v.PeriodStart)
            .ToList();
        var series = values.Select(v => v.Value).ToList();

        return TypedResults.Ok(new IndicatorTrendDto(
            indicator.Id, indicator.Code, indicator.Name, indicator.Unit, indicator.Target, indicator.Direction,
            values.Select(v => ToDto(v, indicator.Frequency)).ToList(),
            series.Count > 0 ? series[^1] : null,
            series.Count > 0 ? Math.Round(series.Average(), 2, MidpointRounding.AwayFromZero) : null,
            IndicatorMath.Trend(series, indicator.Direction)));
    }

    /// <summary>Every active indicator's value for the period containing <paramref name="period"/> (default: last month).</summary>
    private static async Task<IResult> Scorecard(DateOnly? period, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var date = period ?? time.LocalToday().AddMonths(-1);
        var indicators = await db.Set<QualityIndicator>().AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.SortOrder).ThenBy(i => i.Code).ToListAsync(ct);
        var starts = indicators.Select(i => IndicatorMath.PeriodStart(date, i.Frequency)).Distinct().ToList();
        var values = (await db.Set<IndicatorValue>().AsNoTracking().Where(v => starts.Contains(v.PeriodStart)).ToListAsync(ct))
            .ToDictionary(v => (v.IndicatorId, v.PeriodStart));

        return TypedResults.Ok(indicators.Select(i =>
        {
            var start = IndicatorMath.PeriodStart(date, i.Frequency);
            values.TryGetValue((i.Id, start), out var v);
            return new ScorecardRow(i.Id, i.Code, i.Name, i.Category, i.Unit, i.Target, i.Direction, start, IndicatorMath.PeriodLabel(start, i.Frequency),
                v?.Value, v?.MeetsTarget);
        }).ToList());
    }

    private static IndicatorValueDto ToDto(IndicatorValue v, IndicatorFrequency frequency) =>
        new(v.Id, v.PeriodStart, IndicatorMath.PeriodLabel(v.PeriodStart, frequency), v.Numerator, v.Denominator, v.Value, v.MeetsTarget, v.Notes, v.Version);
}
