using System.Linq.Expressions;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Persistence;

public static class QueryExtensions
{
    /// <summary>Executes a count + page query and returns <c>{ items, page, pageSize, totalCount }</c>.</summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(this IQueryable<T> query, PageRequest page, CancellationToken cancellationToken)
    {
        var total = await query.LongCountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResult<T>.Empty(page);
        }

        var items = await query.Skip(page.Skip).Take(page.Take).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, page.Page, page.Take, total);
    }

    /// <summary>
    /// Sorts by a client-supplied key restricted to an allow-list (never reflects arbitrary property
    /// names from the request). Falls back to <paramref name="defaultKey"/>.
    /// </summary>
    public static IQueryable<T> OrderByKey<T>(
        this IQueryable<T> query,
        PageRequest page,
        IReadOnlyDictionary<string, Expression<Func<T, object?>>> allowed,
        Expression<Func<T, object?>> defaultKey,
        bool defaultDescending = false)
    {
        if (!string.IsNullOrWhiteSpace(page.Sort)
            && allowed.FirstOrDefault(kv => string.Equals(kv.Key, page.Sort, StringComparison.OrdinalIgnoreCase)) is { Value: not null } match)
        {
            return page.Desc ? query.OrderByDescending(match.Value) : query.OrderBy(match.Value);
        }

        return defaultDescending ? query.OrderByDescending(defaultKey) : query.OrderBy(defaultKey);
    }

    /// <summary>
    /// The escape character of the patterns <see cref="ToLikePattern"/> and <see cref="ToLikeLiteral"/> build: pass it as
    /// the third argument of <c>EF.Functions.ILike</c>. Without it Npgsql writes <c>ESCAPE ''</c>, the backslashes are then
    /// matched as themselves, and a search for a term with <c>%</c>, <c>_</c> or <c>\</c> (a gateway reference such as
    /// <c>pay_A1B2</c>, a user name such as <c>dr_meera</c>) finds nothing.
    /// </summary>
    public const string LikeEscape = "\\";

    /// <summary>Case-insensitive "contains" for PostgreSQL's ILIKE, with <see cref="LikeEscape"/> as its escape character.</summary>
    public static string ToLikePattern(this string term) => "%" + term.ToLikeLiteral() + "%";

    /// <summary>
    /// <paramref name="term"/> with LIKE's own wildcards (<c>%</c>, <c>_</c>) and its escape character escaped by
    /// <see cref="LikeEscape"/>: as an ILIKE pattern it is a case-insensitive equality.
    /// </summary>
    public static string ToLikeLiteral(this string term) =>
        term.Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
            .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscape + "_", StringComparison.Ordinal);

    /// <summary>Loads an entity by id or returns a NotFound error.</summary>
    public static async Task<Result<T>> FindOrNotFoundAsync<T>(this DbSet<T> set, Guid id, CancellationToken cancellationToken)
        where T : Entity
    {
        var entity = await set.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        return entity is null ? Error.NotFound(typeof(T).Name, id) : entity;
    }

    /// <summary>
    /// Applies the client's last-seen version so EF raises a concurrency conflict if the row changed
    /// since the client loaded it (optimistic concurrency over xmin).
    /// </summary>
    public static void SetExpectedVersion<T>(this DbContext db, T entity, uint expectedVersion)
        where T : class, IVersioned
    {
        db.Entry(entity).Property(nameof(IVersioned.Version)).OriginalValue = expectedVersion;
    }

    /// <summary>
    /// Applies the client's last-seen version and forces the root row to be written, so a concurrent change is detected
    /// even when a request only touches child rows (bill lines, deductions, inclusions, a stock count's lines).
    /// </summary>
    public static void GuardVersion<T>(this DbContext db, T entity, uint expectedVersion)
        where T : AuditableEntity
    {
        db.SetExpectedVersion(entity, expectedVersion);
        db.Entry(entity).Property(nameof(IAuditable.UpdatedAt)).IsModified = true;
    }

    /// <summary>
    /// The same for a record its first save creates (REL-04, <see cref="Http.ConcurrencyExtensions.FirstSaveCreates"/>):
    /// the client sends the version it loaded, or none when there was nothing to load. A save without a version, over a
    /// record that exists, is refused like a stale one (409): someone else created the record meanwhile.
    /// </summary>
    /// <exception cref="DbUpdateConcurrencyException"><paramref name="expectedVersion"/> is null and the record exists.</exception>
    public static void SetExpectedVersion<T>(this DbContext db, T entity, uint? expectedVersion)
        where T : class, IVersioned
    {
        if (db.Entry(entity).State == EntityState.Added)
        {
            return;
        }

        db.SetExpectedVersion(entity, expectedVersion
            ?? throw new DbUpdateConcurrencyException($"A {typeof(T).Name} was saved without a version, and one exists."));
    }

    /// <summary>
    /// <see cref="GuardVersion{T}(DbContext, T, uint)"/> for a record its first save creates: see
    /// <see cref="SetExpectedVersion{T}(DbContext, T, uint?)"/>.
    /// </summary>
    /// <exception cref="DbUpdateConcurrencyException"><paramref name="expectedVersion"/> is null and the record exists.</exception>
    public static void GuardVersion<T>(this DbContext db, T entity, uint? expectedVersion)
        where T : AuditableEntity
    {
        if (db.Entry(entity).State == EntityState.Added)
        {
            return;
        }

        db.SetExpectedVersion(entity, expectedVersion);
        db.Entry(entity).Property(nameof(IAuditable.UpdatedAt)).IsModified = true;
    }
}
