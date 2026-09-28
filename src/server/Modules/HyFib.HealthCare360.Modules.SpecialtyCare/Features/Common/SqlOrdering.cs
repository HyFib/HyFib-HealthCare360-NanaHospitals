using System.Globalization;
using System.Linq.Expressions;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

/// <summary>
/// Enums are stored as text, so <c>ORDER BY</c> on an enum column sorts alphabetically ("Afternoon" before "Morning",
/// "HBsAg" before "Negative"). These helpers rank a column by the enum's declared order instead; the result
/// translates to a SQL <c>CASE</c> expression and follows new enum members automatically.
/// </summary>
internal static class SqlOrdering
{
    public static Expression<Func<TEntity, int>> Rank<TEntity, TEnum>(Expression<Func<TEntity, TEnum>> property)
        where TEnum : struct, Enum
    {
        // Same shape the C# compiler emits for "x.Status == Status.Value", which EF maps through the enum's converter.
        var underlying = Enum.GetUnderlyingType(typeof(TEnum));
        var column = Expression.Convert(property.Body, underlying);
        var values = Enum.GetValues<TEnum>();

        Expression body = Expression.Constant(values.Length);
        for (var i = values.Length - 1; i >= 0; i--)
        {
            var value = Expression.Constant(Convert.ChangeType(values[i], underlying, CultureInfo.InvariantCulture), underlying);
            body = Expression.Condition(Expression.Equal(column, value), Expression.Constant(i), body);
        }

        return Expression.Lambda<Func<TEntity, int>>(body, property.Parameters);
    }

    /// <summary>The same rank as a sort key for <c>OrderByKey</c> allow-lists.</summary>
    public static Expression<Func<TEntity, object?>> RankKey<TEntity, TEnum>(Expression<Func<TEntity, TEnum>> property)
        where TEnum : struct, Enum
    {
        var rank = Rank(property);
        return Expression.Lambda<Func<TEntity, object?>>(Expression.Convert(rank.Body, typeof(object)), rank.Parameters);
    }
}
