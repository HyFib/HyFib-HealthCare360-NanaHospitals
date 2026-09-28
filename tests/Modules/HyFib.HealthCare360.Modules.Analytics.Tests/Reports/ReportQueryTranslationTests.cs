using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Tests.TestSupport;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Reports;

/// <summary>
/// Compiles every query of every report (default inputs, every choice value and every filter) to PostgreSQL SQL against
/// the complete model of all modules, without a database. A query EF Core cannot translate (client evaluation, an
/// unsupported member) fails here instead of in front of a user. The run also proves each report copes with no data.
/// </summary>
public sealed class ReportQueryTranslationTests
{
    private static readonly Guid FilterId = Guid.Parse("0197f5a2-0000-7000-8000-00000000abcd");

    public static TheoryData<string, string> Variants()
    {
        var data = new TheoryData<string, string>();
        foreach (var report in new ReportCatalog().All.Select(r => r.Descriptor))
        {
            data.Add(report.Id, "defaults");
            data.Add(report.Id, "filters");
            foreach (var parameter in report.Parameters.Where(p => p.Kind == ReportParameterKind.Choice))
            {
                foreach (var choice in parameter.Choices!)
                {
                    data.Add(report.Id, $"{parameter.Key}={choice.Value}");
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Report_queries_translate_to_sql_and_handle_an_empty_database(string reportId, string variant)
    {
        var definition = new ReportCatalog().Find(reportId)!;
        var report = definition.Descriptor;
        var arguments = ReportParameterBinder.Bind(report, Inputs(report, variant), ReportTestHarness.Today);
        arguments.IsSuccess.ShouldBeTrue(arguments.Error?.Message);

        using var db = ReportTestHarness.CreateContext(ReportTestHarness.FacilityId);
        var executor = new SqlCapturingExecutor();
        var context = new ReportContext(db, arguments.Value, ReportTestHarness.Now, executor, 5_000);

        var data = await definition.RunAsync(context, TestContext.Current.CancellationToken);

        executor.Sql.ShouldNotBeEmpty();
        executor.Sql.ShouldAllBe(sql => sql.Contains("SELECT", StringComparison.Ordinal));
        var columns = data.Columns ?? report.Columns;
        columns.Count.ShouldBe(report.Columns.Count);
        data.Rows.ShouldAllBe(row => row.Length == columns.Count);
        if (data.Totals is { } totals)
        {
            totals.Length.ShouldBe(columns.Count);
        }

        data.Summary.Select(s => s.Key).ShouldBeUnique();
        if ((data.Chart ?? report.Chart) is { } chart && chart.Type is not (ReportChartType.Table or ReportChartType.Summary))
        {
            columns.Select(c => c.Key).ShouldContain(chart.CategoryKey);
            chart.SeriesKeys.ShouldAllBe(key => columns.Any(c => c.Key == key));
        }
    }

    [Fact]
    public async Task Facility_scoping_and_soft_delete_filters_reach_the_sql()
    {
        var sql = await SqlOfAsync("op-census");

        sql[0].ShouldContain("facility_id");
        sql[0].ShouldContain("is_deleted");
        sql[0].ShouldContain("GROUP BY");
    }

    [Fact]
    public async Task Daily_buckets_are_computed_on_IST_in_sql()
    {
        var sql = await SqlOfAsync("op-visit-trend");

        sql[0].ShouldContain("INTERVAL '330 mins'");
        sql[0].ShouldContain("GROUP BY");
    }

    [Fact]
    public async Task Enum_columns_are_never_sorted_in_sql()
    {
        foreach (var definition in new ReportCatalog().All)
        {
            var sql = await SqlOfAsync(definition.Descriptor.Id);
            foreach (var statement in sql.Where(s => s.Contains("ORDER BY", StringComparison.Ordinal)))
            {
                var orderBy = statement[statement.LastIndexOf("ORDER BY", StringComparison.Ordinal)..];
                orderBy.ShouldNotContain(".status", Case.Sensitive, definition.Descriptor.Id);
                orderBy.ShouldNotContain(".type", Case.Sensitive, definition.Descriptor.Id);
            }
        }
    }

    private static async Task<List<string>> SqlOfAsync(string reportId)
    {
        var definition = new ReportCatalog().Find(reportId)!;
        var arguments = ReportParameterBinder.Bind(definition.Descriptor, null, ReportTestHarness.Today).Value;
        using var db = ReportTestHarness.CreateContext(ReportTestHarness.FacilityId);
        var executor = new SqlCapturingExecutor();
        await definition.RunAsync(new ReportContext(db, arguments, ReportTestHarness.Now, executor, 100), TestContext.Current.CancellationToken);
        return executor.Sql;
    }

    private static Dictionary<string, string?> Inputs(ReportDescriptor report, string variant)
    {
        var inputs = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (variant == "filters")
        {
            foreach (var parameter in report.Parameters)
            {
                switch (parameter.Kind)
                {
                    case ReportParameterKind.Period:
                        inputs[ReportKeys.Period] = "custom";
                        inputs[ReportKeys.From] = "2026-08-01";
                        inputs[ReportKeys.To] = "2026-08-31";
                        break;
                    case ReportParameterKind.Facility:
                        inputs[parameter.Key] = ReportTestHarness.FacilityId.ToString();
                        break;
                    case ReportParameterKind.Number:
                        inputs[parameter.Key] = parameter.Max?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case ReportParameterKind.Choice:
                        inputs[parameter.Key] = parameter.Choices![^1].Value;
                        break;
                    default:
                        inputs[parameter.Key] = FilterId.ToString();
                        break;
                }
            }
        }
        else if (variant.Split('=') is [var key, var value])
        {
            inputs[key] = value;
        }

        return inputs;
    }
}
