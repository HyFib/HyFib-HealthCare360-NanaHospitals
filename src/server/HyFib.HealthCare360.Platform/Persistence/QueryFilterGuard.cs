using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// Refuses a query that would read one of its sources without a query filter that only another source asked to lose.
/// EF Core applies <c>IgnoreQueryFilters</c> to the whole query, not to the source it is written on: a lookup that reads a
/// removed account's name or another facility's department inside a query takes the soft-delete or facility filter off
/// every other source of that query too, and the query returns removed rows or other facilities' rows without a word (a
/// front desk listed every branch's appointments that way). So each source a query reads without a filter says so
/// itself, on the source (<c>db.Set&lt;T&gt;().IgnoreQueryFilters([...])</c>) or around the whole query, and a lookup
/// that must not lift the filter from the rest is read in a query of its own. Checked when EF compiles a query (once per
/// query shape), in every environment: a refused query fails, which is safer than a list that shows what it must not.
/// Sources reached through navigations (<c>Include</c>, <c>a.Patient.Name</c>) are not seen; the modules join by id.
/// </summary>
internal sealed class QueryFilterGuard : IQueryExpressionInterceptor
{
    public static readonly QueryFilterGuard Instance = new();

    private QueryFilterGuard()
    {
    }

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        var lifted = Lifted(queryExpression);
        return lifted.Count == 0
            ? queryExpression
            : throw new InvalidOperationException(
                "A query would read " + string.Join(", ", lifted.Select(l => $"{l.Source} without its {string.Join(" and ", l.Filters.Select(f => $"'{f}'"))} filter")) +
                ", which only another of its sources asked to ignore: EF Core ignores a query filter for the whole query. Read that other source in a query of " +
                "its own, or write IgnoreQueryFilters on each source that is to be read without the filter.");
    }

    /// <summary>The sources of <paramref name="query"/> that would lose a filter only because another source ignores it.</summary>
    internal static IReadOnlyList<LiftedFilter> Lifted(Expression query)
    {
        var sources = new Sources();
        sources.Visit(query);
        if (sources.Ignores.Count == 0)
        {
            return [];
        }

        var ignored = Ignore.Union(sources.Ignores);
        return sources.Roots
            .Select(root => new LiftedFilter(
                root.Type.DisplayName(),
                [.. root.Type.GetRootType().GetDeclaredQueryFilters()
                    .Where(filter => ignored.Covers(filter) && !root.Around.Covers(filter))
                    .Select(filter => filter.Key ?? "anonymous")]))
            .Where(lifted => lifted.Filters.Count > 0)
            .DistinctBy(lifted => (lifted.Source, string.Join(',', lifted.Filters)))
            .ToList();
    }

    internal sealed record LiftedFilter(string Source, IReadOnlyList<string> Filters);

    /// <summary>What an <c>IgnoreQueryFilters</c> call ignores: every filter, or the named ones.</summary>
    private sealed record Ignore(bool All, IReadOnlySet<string> Keys)
    {
        public static readonly Ignore Nothing = new(false, new HashSet<string>(StringComparer.Ordinal));

        public bool Covers(IQueryFilter filter) => All || (filter.Key is { } key && Keys.Contains(key));

        public static Ignore Union(IEnumerable<Ignore> ignores)
        {
            var list = ignores.ToList();
            return list.Any(i => i.All)
                ? new Ignore(true, Nothing.Keys)
                : new Ignore(false, list.SelectMany(i => i.Keys).ToHashSet(StringComparer.Ordinal));
        }

        public static Ignore Of(MethodCallExpression call) =>
            call.Arguments.Count == 1
                ? new Ignore(true, Nothing.Keys)
                : call.Arguments[1] is ConstantExpression { Value: IEnumerable<string> keys }
                    ? new Ignore(false, keys.ToHashSet(StringComparer.Ordinal))
                    : new Ignore(true, Nothing.Keys); // not seen yet: EF passes the names as a constant
    }

    /// <summary>The query's sources, each with the <c>IgnoreQueryFilters</c> calls written around it, and every such call.</summary>
    private sealed class Sources : ExpressionVisitor
    {
        private readonly List<Ignore> _around = [];

        public List<Ignore> Ignores { get; } = [];

        public List<(IEntityType Type, Ignore Around)> Roots { get; } = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(EntityFrameworkQueryableExtensions)
                || node.Method.Name != nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters))
            {
                return base.VisitMethodCall(node);
            }

            var ignore = Ignore.Of(node);
            Ignores.Add(ignore);
            _around.Add(ignore);
            Visit(node.Arguments[0]);
            _around.RemoveAt(_around.Count - 1);
            return node;
        }

        protected override Expression VisitExtension(Expression node)
        {
            switch (node)
            {
                case EntityQueryRootExpression root:
                    Roots.Add((root.EntityType, Ignore.Union(_around)));
                    return node;
                case QueryRootExpression:
                    return node;
                default:
                    try
                    {
                        return base.VisitExtension(node);
                    }
                    catch (ArgumentException)
                    {
                        // A node EF has not reduced and that cannot visit its children holds no source we could check.
                        return node;
                    }
            }
        }
    }
}
