using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Persistence;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Reports;

/// <summary>The limits a report's queries run under, on rows held in memory instead of a database.</summary>
public sealed class ReportContextTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rows_past_the_cap_are_dropped_and_mark_the_report_truncated()
    {
        using var db = ReportTestHarness.CreateContext(ReportTestHarness.FacilityId);
        var context = Context(db, rowCap: 100);

        (await context.ListAsync(Enumerable.Range(0, 100).AsQueryable(), Ct)).Count.ShouldBe(100);
        context.Truncated.ShouldBeFalse("exactly the cap is complete");

        (await context.ListAsync(Enumerable.Range(0, 101).AsQueryable(), Ct)).Count.ShouldBe(100);
        context.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task Look_ups_read_as_many_names_as_the_report_has_rows_and_a_cut_look_up_marks_it_truncated()
    {
        using var db = ReportTestHarness.CreateContext(ReportTestHarness.FacilityId);
        var names = Enumerable.Range(0, 30_000).AsQueryable();

        var extract = Context(db, ReportRunner.ExtractRowCap);
        (await extract.LookupAsync(names, Ct)).Count.ShouldBe(30_000);
        extract.Truncated.ShouldBeFalse("an extract's rows all get their names");

        var screen = Context(db, ReportRunner.RowCap);
        (await screen.LookupAsync(names, Ct)).Count.ShouldBe(20_000);
        screen.Truncated.ShouldBeTrue("rows past the look-up's cap would have no name");
    }

    private static ReportContext Context(HospitalDbContext db, int rowCap)
    {
        var report = new ReportCatalog().Find("revenue-by-department")!.Descriptor;
        var arguments = ReportParameterBinder.Bind(report, new Dictionary<string, string?>(), ReportTestHarness.Today);
        return new ReportContext(db, arguments.Value, ReportTestHarness.Now, new InMemoryExecutor(), rowCap);
    }

    /// <summary>Runs the query on the rows it was built over (no database).</summary>
    private sealed class InMemoryExecutor : IReportQueryExecutor
    {
        public Task<List<T>> ListAsync<T>(IQueryable<T> query, CancellationToken ct) => Task.FromResult(query.ToList());
    }
}
