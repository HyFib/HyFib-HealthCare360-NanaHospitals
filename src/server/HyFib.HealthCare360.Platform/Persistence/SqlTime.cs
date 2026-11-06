using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// Times as the APIs serve them, for queries that order or compare by them. PostgreSQL keeps microseconds; a FHIR
/// instant, and every time the APIs serve, has milliseconds. A query that orders by what was served must not tell apart
/// two times its reader sees as the same.
/// </summary>
public static class SqlTime
{
    /// <summary>
    /// The time cut to the millisecond, as PostgreSQL's <c>date_trunc('milliseconds', …)</c> in a query, and this body
    /// when a query runs in memory.
    /// </summary>
    public static DateTimeOffset ToMilliseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Offset);

    /// <summary>Maps <see cref="ToMilliseconds"/> to <c>date_trunc</c> for the model.</summary>
    internal static void Map(ModelBuilder modelBuilder) =>
        modelBuilder.HasDbFunction(typeof(SqlTime).GetMethod(nameof(ToMilliseconds))!)
            .HasTranslation(arguments => new SqlFunctionExpression(
                "date_trunc",
                [new SqlFragmentExpression("'milliseconds'"), arguments[0]],
                nullable: true,
                argumentsPropagateNullability: [false, true],
                typeof(DateTimeOffset),
                arguments[0].TypeMapping));
}
