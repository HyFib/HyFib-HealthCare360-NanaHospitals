using System.Globalization;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Platform.Numbering;

public enum NumberResetPolicy
{
    Never = 0,
    Yearly,
    Monthly,
    Daily,
}

/// <summary>
/// A human-facing number sequence (UHID, bill number, accession number…). Allocation increments the
/// row inside the caller's transaction, so numbers are gap-free when the caller commits atomically.
/// </summary>
public sealed class NumberSeries : AuditableEntity
{
    /// <summary>Null = shared across facilities (for example UHID across a hospital chain).</summary>
    public Guid? FacilityId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Format { get; set; } = "{FAC}{YY}{SEQ:6}";
    public long CurrentValue { get; set; }
    public NumberResetPolicy ResetPolicy { get; set; }
    public string? CurrentPeriod { get; set; }
}

internal sealed class NumberSeriesConfiguration : IEntityTypeConfiguration<NumberSeries>
{
    public void Configure(EntityTypeBuilder<NumberSeries> b)
    {
        b.ToTable("number_series", "platform");
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Format).HasMaxLength(64);
        b.Property(x => x.CurrentPeriod).HasMaxLength(16);
        b.HasIndex(x => new { x.Key, x.FacilityId }).IsUnique().AreNullsDistinct(false);
    }
}

public interface INumberSeries
{
    /// <summary>
    /// Allocates the next formatted number for <paramref name="key"/>. Call inside the same transaction
    /// as the document that uses it. The default format/reset apply only when the series is first created.
    /// </summary>
    Task<string> NextAsync(string key, Guid? facilityId, string defaultFormat, NumberResetPolicy defaultReset, CancellationToken cancellationToken);
}

internal sealed partial class NumberSeriesService(HospitalDbContext db, IMemoryCache cache, TimeProvider time) : INumberSeries
{
    private sealed record Allocation(long Value, string Format, string? FacilityCode);

    public async Task<string> NextAsync(string key, Guid? facilityId, string defaultFormat, NumberResetPolicy defaultReset, CancellationToken cancellationToken)
    {
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), IndiaTimeZone.Value);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var series = await db.Set<NumberSeries>()
                .Where(s => s.Key == key && s.FacilityId == facilityId)
                .Select(s => new { s.ResetPolicy })
                .FirstOrDefaultAsync(cancellationToken);

            if (series is null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO platform.number_series (id, facility_id, key, format, current_value, reset_policy, created_at, is_deleted)
                    VALUES ({Guid.CreateVersion7()}, {facilityId}, {key}, {defaultFormat}, 0, {defaultReset.ToString()}, {time.GetUtcNow()}, false)
                    ON CONFLICT (key, facility_id) DO NOTHING
                    """, cancellationToken);
                continue;
            }

            var period = PeriodFor(series.ResetPolicy, now);
            var allocated = await db.Database.SqlQuery<AllocatedRow>($"""
                UPDATE platform.number_series
                   SET current_value = CASE WHEN current_period IS NOT DISTINCT FROM {period} THEN current_value + 1 ELSE 1 END,
                       current_period = {period},
                       updated_at = {time.GetUtcNow()}
                 WHERE key = {key} AND facility_id IS NOT DISTINCT FROM {facilityId}
                RETURNING current_value AS "Value", format AS "Format"
                """).ToListAsync(cancellationToken);

            if (allocated.Count == 1)
            {
                var facilityCode = await FacilityCodeAsync(facilityId, cancellationToken);
                return Render(allocated[0].Format, allocated[0].Value, facilityCode, now);
            }
        }

        throw new InvalidOperationException($"Could not allocate a number for series '{key}'.");
    }

    /// <summary>Renders tokens: {FAC} {YYYY} {YY} {MM} {DD} {SEQ} {SEQ:n}.</summary>
    internal static string Render(string format, long value, string? facilityCode, DateTimeOffset localNow) =>
        TokenRegex().Replace(format, match => match.Groups["name"].Value switch
        {
            "FAC" => facilityCode ?? string.Empty,
            "YYYY" => localNow.ToString("yyyy", CultureInfo.InvariantCulture),
            "YY" => localNow.ToString("yy", CultureInfo.InvariantCulture),
            "MM" => localNow.ToString("MM", CultureInfo.InvariantCulture),
            "DD" => localNow.ToString("dd", CultureInfo.InvariantCulture),
            "SEQ" => match.Groups["width"].Success
                ? value.ToString(CultureInfo.InvariantCulture).PadLeft(int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture), '0')
                : value.ToString(CultureInfo.InvariantCulture),
            _ => match.Value,
        });

    internal static string? PeriodFor(NumberResetPolicy policy, DateTimeOffset localNow) => policy switch
    {
        NumberResetPolicy.Yearly => localNow.ToString("yyyy", CultureInfo.InvariantCulture),
        NumberResetPolicy.Monthly => localNow.ToString("yyyyMM", CultureInfo.InvariantCulture),
        NumberResetPolicy.Daily => localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        _ => null,
    };

    private async Task<string?> FacilityCodeAsync(Guid? facilityId, CancellationToken cancellationToken)
    {
        if (facilityId is not { } id)
        {
            return await cache.GetOrCreateAsync("facility-code:default", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
                return await db.Set<Facility>().IgnoreQueryFilters().OrderBy(f => f.CreatedAt).Select(f => f.Code).FirstOrDefaultAsync(cancellationToken);
            });
        }

        return await cache.GetOrCreateAsync($"facility-code:{id:N}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await db.Set<Facility>().IgnoreQueryFilters().Where(f => f.Id == id).Select(f => f.Code).FirstOrDefaultAsync(cancellationToken);
        });
    }

    private static readonly Lazy<TimeZoneInfo> IndiaTimeZone = new(() =>
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    });

    [GeneratedRegex(@"\{(?<name>FAC|YYYY|YY|MM|DD|SEQ)(:(?<width>\d{1,2}))?\}")]
    private static partial Regex TokenRegex();

    private sealed record AllocatedRow(long Value, string Format);
}

public sealed record NumberSeriesDto(Guid Id, Guid? FacilityId, string Key, string Format, long CurrentValue, NumberResetPolicy ResetPolicy, string? CurrentPeriod, uint Version);

public sealed record UpdateNumberSeriesRequest(string Format, NumberResetPolicy ResetPolicy, uint Version);

internal static class NumberSeriesEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/number-series").WithTags("Administration");

        group.MapGet("/", async (HospitalDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await db.Set<NumberSeries>().OrderBy(s => s.Key)
                    .Select(s => new NumberSeriesDto(s.Id, s.FacilityId, s.Key, s.Format, s.CurrentValue, s.ResetPolicy, s.CurrentPeriod, s.Version))
                    .ToListAsync(ct)))
            .RequirePermission(PlatformPermissions.NumberingManage);

        group.MapPut("/{id:guid}", async (Guid id, UpdateNumberSeriesRequest request, HospitalDbContext db, CancellationToken ct) =>
            {
                if (!request.Format.Contains("{SEQ", StringComparison.Ordinal))
                {
                    return Error.Field("format", "The format must contain a {SEQ} or {SEQ:n} token.").ToProblem();
                }

                var series = await db.Set<NumberSeries>().FirstOrDefaultAsync(s => s.Id == id, ct);
                if (series is null)
                {
                    return Error.NotFound(nameof(NumberSeries), id).ToProblem();
                }

                db.SetExpectedVersion(series, request.Version);
                series.Format = request.Format.Trim();
                series.ResetPolicy = request.ResetPolicy;
                await db.SaveChangesAsync(ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(PlatformPermissions.NumberingManage);
    }
}
