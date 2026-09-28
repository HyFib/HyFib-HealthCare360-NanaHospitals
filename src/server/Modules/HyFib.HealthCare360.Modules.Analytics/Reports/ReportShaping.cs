using System.Globalization;

namespace HyFib.HealthCare360.Modules.Analytics.Reports;

/// <summary>One chart series: a numeric column's values in category order.</summary>
public sealed record ReportChartSeries(string Key, string Label, ReportValueType Type, IReadOnlyList<decimal?> Values);

/// <summary>Ready-to-draw chart data (the staff app renders it with ECharts; dashboards reuse it).</summary>
public sealed record ReportChartData(
    ReportChartType Type,
    string CategoryKey,
    string CategoryLabel,
    ReportValueType CategoryType,
    IReadOnlyList<string> Categories,
    IReadOnlyList<ReportChartSeries> Series,
    bool TimeSeries,
    IReadOnlyList<string?>? Keys = null);

/// <summary>
/// Pure row shaping used by the report screen, exports and dashboards (unit-tested): type-aware sorting on any column
/// (in memory, over rows the report already produced, so no client input ever reaches SQL), paging and chart data.
/// </summary>
public static class ReportShaping
{
    public const string OthersLabel = "Others";

    /// <summary>Sorts rows by a column: numbers numerically, dates chronologically, text case-insensitively; blanks last.</summary>
    public static IReadOnlyList<object?[]> Sort(IReadOnlyList<object?[]> rows, int columnIndex, bool descending) =>
        SortOrder(rows, columnIndex, descending).Select(i => rows[i]).ToList();

    /// <summary>The row order <see cref="Sort"/> produces, as indexes (so row keys can follow their rows). Stable.</summary>
    public static IReadOnlyList<int> SortOrder(IReadOnlyList<object?[]> rows, int columnIndex, bool descending)
    {
        var indexes = Enumerable.Range(0, rows.Count);
        if (columnIndex < 0)
        {
            return indexes.ToList();
        }

        var withValue = indexes.Where(i => rows[i][columnIndex] is not null);
        var ordered = descending
            ? withValue.OrderByDescending(i => rows[i][columnIndex], ValueComparer.Instance)
            : withValue.OrderBy(i => rows[i][columnIndex], ValueComparer.Instance);
        return ordered.Concat(indexes.Where(i => rows[i][columnIndex] is null)).ToList();
    }

    public static IReadOnlyList<object?[]> Page(IReadOnlyList<object?[]> rows, int page, int pageSize) =>
        rows.Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize).ToList();

    /// <summary>
    /// Builds chart data from all rows. Time series keep their order; other charts show the largest categories
    /// (by the first series) and fold the rest into "Others" when the series are additive (counts and money).
    /// </summary>
    public static ReportChartData? Chart(
        IReadOnlyList<ReportColumn> columns,
        IReadOnlyList<object?[]> rows,
        ReportChartHint? hint,
        ReportChartType? overrideType = null,
        IReadOnlyList<string?>? rowKeys = null)
    {
        if (hint is null)
        {
            return null;
        }

        var type = overrideType ?? hint.Type;
        if (type is ReportChartType.Table or ReportChartType.Summary)
        {
            return null;
        }

        var categoryIndex = IndexOf(columns, hint.CategoryKey);
        var seriesIndexes = hint.SeriesKeys.Select(k => IndexOf(columns, k)).Where(i => i >= 0).ToList();
        if (categoryIndex < 0 || seriesIndexes.Count == 0)
        {
            return null;
        }

        if (type == ReportChartType.Pie)
        {
            seriesIndexes = [seriesIndexes[0]];
        }

        var topN = Math.Max(hint.TopN, 3);
        var keys = rowKeys is not null && rowKeys.Count == rows.Count ? rowKeys : null;
        var order = hint.TimeSeries
            ? Enumerable.Range(0, rows.Count).ToList()
            : Enumerable.Range(0, rows.Count).OrderByDescending(i => ToDecimal(rows[i][seriesIndexes[0]]) ?? decimal.MinValue).ToList();

        var categories = new List<string>();
        var categoryKeys = new List<string?>();
        var values = seriesIndexes.Select(_ => new List<decimal?>()).ToList();
        var additive = seriesIndexes.All(i => IsAdditive(columns[i].Type));
        var shown = !hint.TimeSeries && order.Count > topN ? (additive ? topN - 1 : topN) : order.Count;
        foreach (var rowIndex in order.Take(shown))
        {
            var row = rows[rowIndex];
            categories.Add(CategoryText(row[categoryIndex]));
            categoryKeys.Add(keys?[rowIndex]);
            for (var s = 0; s < seriesIndexes.Count; s++)
            {
                values[s].Add(ToDecimal(row[seriesIndexes[s]]));
            }
        }

        if (!hint.TimeSeries && additive && order.Count > shown)
        {
            categories.Add(OthersLabel);
            categoryKeys.Add(null);
            for (var s = 0; s < seriesIndexes.Count; s++)
            {
                var index = seriesIndexes[s];
                values[s].Add(order.Skip(shown).Sum(i => ToDecimal(rows[i][index]) ?? 0m));
            }
        }

        return new ReportChartData(
            type,
            columns[categoryIndex].Key,
            columns[categoryIndex].Label,
            columns[categoryIndex].Type,
            categories,
            seriesIndexes.Select((index, s) => new ReportChartSeries(columns[index].Key, columns[index].Label, columns[index].Type, values[s])).ToList(),
            hint.TimeSeries,
            keys is null ? null : categoryKeys);
    }

    public static decimal? ToDecimal(object? value) => value switch
    {
        null => null,
        decimal d => d,
        int i => i,
        long l => l,
        double d => double.IsFinite(d) ? (decimal)d : null,
        float f => float.IsFinite(f) ? (decimal)f : null,
        _ => null,
    };

    /// <summary>Category axis text: ISO dates (the app formats them), plain text otherwise.</summary>
    public static string CategoryText(object? value) => value switch
    {
        null => "—",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "—",
    };

    private static bool IsAdditive(ReportValueType type) => type is ReportValueType.Integer or ReportValueType.Money or ReportValueType.Decimal;

    private static int IndexOf(IReadOnlyList<ReportColumn> columns, string key)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Orders mixed cell values: numbers, then dates/times, then text.</summary>
    private sealed class ValueComparer : IComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (ToDecimal(x) is { } a && ToDecimal(y) is { } b)
            {
                return a.CompareTo(b);
            }

            return (x, y) switch
            {
                (DateOnly d1, DateOnly d2) => d1.CompareTo(d2),
                (DateTimeOffset t1, DateTimeOffset t2) => t1.CompareTo(t2),
                (string s1, string s2) => StringComparer.OrdinalIgnoreCase.Compare(s1, s2),
                _ => StringComparer.OrdinalIgnoreCase.Compare(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture)),
            };
        }
    }
}
