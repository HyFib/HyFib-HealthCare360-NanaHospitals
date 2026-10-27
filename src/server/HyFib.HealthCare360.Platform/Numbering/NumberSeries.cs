using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Platform.Numbering;

/// <summary>When a series' counter starts again from 1 (in the hospital's time zone).</summary>
public enum NumberResetPolicy
{
    Never = 0,
    Yearly,
    Monthly,
    Daily,
}

/// <summary>
/// The counter and format of one <see cref="NumberSeriesDefinition"/> (for one facility, or shared by all). Allocation
/// increments the row inside the caller's transaction, so numbers are gap-free when the caller commits atomically.
/// A row appears when an administrator sets the format or when the first number is issued, whichever comes first.
/// </summary>
public sealed class NumberSeries : AuditableEntity
{
    /// <summary>Null = shared across facilities (for example UHID across a hospital chain).</summary>
    public Guid? FacilityId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Format { get; set; } = "{FAC}{YY}{SEQ:6}";

    /// <summary>The last number issued in <see cref="CurrentPeriod"/> (0 before the first).</summary>
    public long CurrentValue { get; set; }
    public NumberResetPolicy ResetPolicy { get; set; }

    /// <summary>The period <see cref="CurrentValue"/> counts in: "yyyy", "yyyyMM", "yyyyMMdd", or null when it never restarts.</summary>
    public string? CurrentPeriod { get; set; }
}

internal sealed class NumberSeriesConfiguration : IEntityTypeConfiguration<NumberSeries>
{
    public void Configure(EntityTypeBuilder<NumberSeries> b)
    {
        b.ToTable("number_series", "platform");
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Format).HasMaxLength(NumberFormatRules.FormatColumnLength);
        b.Property(x => x.CurrentPeriod).HasMaxLength(16);
        b.HasIndex(x => new { x.Key, x.FacilityId }).IsUnique().AreNullsDistinct(false);
    }
}

public interface INumberSeries
{
    /// <summary>
    /// Allocates the next formatted number of <paramref name="series"/>. Call inside the same transaction as the document
    /// that uses it. <paramref name="facilityId"/> is the facility the document belongs to; it is ignored for a series that
    /// all facilities share. The definition's default format and reset apply until an administrator changes them.
    /// </summary>
    Task<string> NextAsync(NumberSeriesDefinition series, Guid? facilityId, CancellationToken cancellationToken);

    /// <summary>
    /// Allocates the next <paramref name="count"/> numbers of <paramref name="series"/> at once, in order, for use later
    /// (a block kept for registering patients on paper while HC360 is down). They are issued as any other number is, so
    /// the series never issues them again; the caller keeps them.
    /// </summary>
    Task<IReadOnlyList<string>> ReserveAsync(NumberSeriesDefinition series, Guid? facilityId, int count, CancellationToken cancellationToken);
}

internal sealed class NumberSeriesService(HospitalDbContext db, NumberSeriesCatalog catalog, IMemoryCache cache, TimeProvider time) : INumberSeries
{
    public async Task<string> NextAsync(NumberSeriesDefinition series, Guid? facilityId, CancellationToken cancellationToken) =>
        (await AllocateAsync(series, facilityId, 1, cancellationToken))[0];

    public Task<IReadOnlyList<string>> ReserveAsync(NumberSeriesDefinition series, Guid? facilityId, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return AllocateAsync(series, facilityId, count, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> AllocateAsync(NumberSeriesDefinition series, Guid? facilityId, int count, CancellationToken cancellationToken)
    {
        // Every series a module issues is on the administrators' list, so it can be set up before its first number.
        if (!series.Hidden && !catalog.IsDeclared(series))
        {
            throw new InvalidOperationException(
                $"Number series '{series.Key}' is not declared. Add its definition to the module's NumberSeries list.");
        }

        if (series.PerFacility && facilityId is null)
        {
            throw new InvalidOperationException($"Number series '{series.Key}' counts per facility: say which facility the number is for.");
        }

        var scope = series.PerFacility ? facilityId : null;
        var now = NumberFormat.ToHospitalTime(time.GetUtcNow());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var row = await db.Set<NumberSeries>()
                .Where(s => s.Key == series.Key && s.FacilityId == scope)
                .Select(s => new { s.ResetPolicy })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO platform.number_series (id, facility_id, key, format, current_value, reset_policy, created_at, is_deleted)
                    VALUES ({Guid.CreateVersion7()}, {scope}, {series.Key}, {series.DefaultFormat}, 0, {series.DefaultReset.ToString()}, {time.GetUtcNow()}, false)
                    ON CONFLICT (key, facility_id) DO NOTHING
                    """, cancellationToken);
                continue;
            }

            var period = NumberFormat.PeriodFor(row.ResetPolicy, now);
            var allocated = await db.Database.SqlQuery<AllocatedRow>($"""
                UPDATE platform.number_series
                   SET current_value = CASE WHEN current_period IS NOT DISTINCT FROM {period} THEN current_value + {count} ELSE {count} END,
                       current_period = {period},
                       updated_at = {time.GetUtcNow()}
                 WHERE key = {series.Key} AND facility_id IS NOT DISTINCT FROM {scope}
                RETURNING current_value AS "Value", format AS "Format"
                """).ToListAsync(cancellationToken);

            if (allocated.Count == 1)
            {
                var facilityCode = await FacilityCodeAsync(scope, cancellationToken);
                var last = allocated[0].Value;
                return [.. Enumerable.Range(0, count).Select(i => NumberFormat.Render(allocated[0].Format, last - count + 1 + i, facilityCode, now))];
            }
        }

        throw new InvalidOperationException($"Could not allocate a number for series '{series.Key}'.");
    }

    /// <summary>
    /// The code {FAC} stands for: the facility's own, or for a series all facilities share, the first facility's (the
    /// hospital's main one).
    /// </summary>
    internal static Task<string?> FacilityCodeAsync(HospitalDbContext db, Guid? facilityId, CancellationToken cancellationToken) =>
        facilityId is { } id
            ? db.Set<Facility>().IgnoreQueryFilters().Where(f => f.Id == id).Select(f => f.Code).FirstOrDefaultAsync(cancellationToken)
            : db.Set<Facility>().IgnoreQueryFilters().OrderBy(f => f.CreatedAt).Select(f => f.Code).FirstOrDefaultAsync(cancellationToken);

    private async Task<string?> FacilityCodeAsync(Guid? facilityId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(facilityId is { } id ? $"facility-code:{id:N}" : "facility-code:default", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await FacilityCodeAsync(db, facilityId, cancellationToken);
        });

    private sealed record AllocatedRow(long Value, string Format);
}
