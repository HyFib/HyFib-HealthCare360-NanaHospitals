using System.Linq.Expressions;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Orders worklists by clinical urgency: STAT, then urgent, then routine. Enums are stored as text, so ordering by the
/// priority column in SQL would sort alphabetically (Routine, Stat, Urgent) and bury STAT samples below urgent ones.
/// The rank is therefore compared explicitly, which PostgreSQL evaluates as boolean sort keys.
/// </summary>
internal static class UrgencyOrdering
{
    public static IOrderedQueryable<T> OrderByUrgency<T>(this IQueryable<T> query, Expression<Func<T, OrderPriority>> priority) =>
        query.OrderByDescending(Is(priority, OrderPriority.Stat)).ThenByDescending(Is(priority, OrderPriority.Urgent));

    public static IOrderedQueryable<T> ThenByUrgency<T>(this IOrderedQueryable<T> query, Expression<Func<T, OrderPriority>> priority) =>
        query.ThenByDescending(Is(priority, OrderPriority.Stat)).ThenByDescending(Is(priority, OrderPriority.Urgent));

    private static Expression<Func<T, bool>> Is<T>(Expression<Func<T, OrderPriority>> priority, OrderPriority value) =>
        Expression.Lambda<Func<T, bool>>(Expression.Equal(priority.Body, Expression.Constant(value)), priority.Parameters);
}
