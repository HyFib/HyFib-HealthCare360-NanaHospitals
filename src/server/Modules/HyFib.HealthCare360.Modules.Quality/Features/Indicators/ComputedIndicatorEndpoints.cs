using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Modules.Quality.Services.Indicators;

namespace HyFib.HealthCare360.Modules.Quality.Features.Indicators;

/// <summary>Computes the period containing <see cref="Period"/> (default: last month); a period still running is computed to today.</summary>
public sealed record ComputeIndicatorsRequest(DateOnly? Period);

public sealed class ComputeIndicatorsRequestValidator : AbstractValidator<ComputeIndicatorsRequest>
{
    public ComputeIndicatorsRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Period).Must(p => p is null || p <= time.LocalToday()).WithMessage("Choose a current or past period.");
    }
}

/// <summary>One record behind the value; the patient by UHID (a restricted record shows none).</summary>
public sealed record DrilldownRecord(MeasureRole Role, string Kind, string Reference, string? Uhid, DateTimeOffset At, decimal? Minutes, string? Detail);

/// <summary>
/// The records behind a computed indicator's value for a period, computed now. The stored value is shown next to it: the two
/// differ when records were added or corrected since the value was computed.
/// </summary>
public sealed record IndicatorDrilldown(
    Guid IndicatorId,
    string Code,
    string Name,
    string NumeratorDefinition,
    string DenominatorDefinition,
    IndicatorUnit Unit,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string PeriodLabel,
    decimal Numerator,
    decimal Denominator,
    decimal? Value,
    bool? MeetsTarget,
    IndicatorValueDto? Stored,
    string? Note,
    bool Unavailable,
    int RecordCount,
    IReadOnlyList<DrilldownRecord> Records);

/// <summary>
/// /api/v1/quality/indicators — the indicators HC360 computes from its own records: compute a period now (the monthly job does
/// it for the month just ended), and the drill-down to the records behind a value.
/// </summary>
internal static class ComputedIndicatorEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/indicators").WithTags("Quality").RequirePermission(QualityPermissions.IndicatorsManage);
        group.MapPost("/compute", (ComputeIndicatorsRequest r, IndicatorComputation c, ICurrentUser u, TimeProvider t, CancellationToken ct) => Compute(null, r, c, u, t, ct))
            .WithValidation<ComputeIndicatorsRequest>();
        group.MapPost("/{id:guid}/compute", (Guid id, ComputeIndicatorsRequest r, IndicatorComputation c, ICurrentUser u, TimeProvider t, CancellationToken ct) => Compute(id, r, c, u, t, ct))
            .WithValidation<ComputeIndicatorsRequest>();
        group.MapGet("/{id:guid}/drilldown", Drilldown);
    }

    private static async Task<IResult> Compute(Guid? id, ComputeIndicatorsRequest request, IndicatorComputation computation, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var results = await computation.ComputeAsync(facilityId, request.Period ?? time.LocalToday().AddMonths(-1), id, ct);
        if (id is not null && results.Count == 0)
        {
            return Error.Rule("indicator.not_computed", "HC360 does not compute this indicator: its values are entered by hand.").ToProblem();
        }

        return TypedResults.Ok(results);
    }

    private static async Task<IResult> Drilldown(
        Guid id, DateOnly? period, HospitalDbContext db, IndicatorMeasures measures, ICurrentUser user, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var indicator = await db.Set<QualityIndicator>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (indicator is null)
        {
            return Error.NotFound(nameof(QualityIndicator), id).ToProblem();
        }

        if (indicator.ComputedBy is not { } key)
        {
            return Error.Rule("indicator.not_computed", "HC360 does not compute this indicator: its values are entered by hand.").ToProblem();
        }

        var today = time.LocalToday();
        var day = period ?? today.AddMonths(-1);
        if (day > today)
        {
            return Error.Field("period", "Choose a current or past period.").ToProblem();
        }

        var p = IndicatorComputation.PeriodOf(facilityId, day, indicator.Frequency, today);
        var result = await measures.ComputeAsync(key, p, ct);
        var stored = await db.Set<IndicatorValue>().AsNoTracking().FirstOrDefaultAsync(v => v.IndicatorId == id && v.PeriodStart == p.From, ct);

        var patientIds = result.Records.Where(r => r.PatientId is not null).Select(r => r.PatientId!.Value).Distinct().ToList();
        var patients = await db.Set<Patient>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(x => patientIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Uhid, x.IsVip })
            .ToDictionaryAsync(x => x.Id, ct);
        var records = result.Records.Select(r => new DrilldownRecord(r.Role, r.Kind, r.Reference,
                r.PatientId is { } pid && patients.TryGetValue(pid, out var patient) ? patient.IsVip ? "Restricted record" : patient.Uhid : null,
                r.At, r.Minutes, r.Detail))
            .ToList();

        if (patientIds.Count > 0)
        {
            audit.Add(new AuditEntry(AuditCategories.PhiAccess, "quality.indicator.drilldown",
                $"Viewed the records behind {indicator.Code} for {IndicatorMath.PeriodLabel(p.From, indicator.Frequency)} ({patientIds.Count} patients listed)")
            {
                EntityType = nameof(QualityIndicator),
                EntityId = indicator.Id.ToString(),
            });
            foreach (var patientId in patientIds.Where(pid => patients.ContainsKey(pid) && !patients[pid].IsVip))
            {
                audit.Add(new AuditEntry(AuditCategories.PhiAccess, "patient.accessed", $"Listed behind the quality indicator {indicator.Code}")
                {
                    PatientId = patientId,
                    EntityType = nameof(QualityIndicator),
                    EntityId = indicator.Id.ToString(),
                });
            }

            await db.SaveChangesAsync(ct);
        }

        var value = IndicatorMath.Compute(result.Numerator, result.Denominator, indicator.Multiplier);
        return TypedResults.Ok(new IndicatorDrilldown(
            indicator.Id, indicator.Code, indicator.Name, indicator.NumeratorDefinition, indicator.DenominatorDefinition, indicator.Unit, p.From, p.To,
            IndicatorMath.PeriodLabel(p.From, indicator.Frequency), result.Numerator, result.Denominator, value,
            IndicatorMath.MeetsTarget(value, indicator.Target, indicator.Direction),
            stored is null ? null : IndicatorEndpoints.ToDto(stored, indicator.Frequency), result.Note, result.Unavailable, result.Records.Count, records));
    }
}
