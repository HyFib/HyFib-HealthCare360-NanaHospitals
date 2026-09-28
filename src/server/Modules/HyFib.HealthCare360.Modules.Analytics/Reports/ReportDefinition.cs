namespace HyFib.HealthCare360.Modules.Analytics.Reports;

/// <summary>
/// One MIS report: its catalogue entry and its query. Queries project and aggregate in PostgreSQL (never loading
/// entities to count them) and run through <see cref="ReportContext.ListAsync{T}"/>, which caps rows and lets the tests
/// check that every query translates to SQL without a database.
/// </summary>
internal abstract class ReportDefinition
{
    public abstract ReportDescriptor Descriptor { get; }

    public abstract Task<ReportData> RunAsync(ReportContext context, CancellationToken ct);
}

/// <summary>Executes report queries. Production uses EF Core; tests capture the SQL instead.</summary>
internal interface IReportQueryExecutor
{
    Task<List<T>> ListAsync<T>(IQueryable<T> query, CancellationToken ct);
}

internal sealed class EfReportQueryExecutor : IReportQueryExecutor
{
    public static readonly EfReportQueryExecutor Instance = new();

    public Task<List<T>> ListAsync<T>(IQueryable<T> query, CancellationToken ct) => query.ToListAsync(ct);
}

/// <summary>
/// What a report runs against: a facility-scoped (and possibly read-replica) <see cref="HospitalDbContext"/>, the validated
/// arguments and "now". The context's facility filter already limits every facility-owned table, so reports never filter
/// by facility themselves.
/// </summary>
internal sealed class ReportContext(HospitalDbContext db, ReportArguments args, DateTimeOffset now, IReportQueryExecutor executor, int rowCap)
{
    public HospitalDbContext Db => db;

    public ReportArguments Args => args;

    public DateTimeOffset Now => now;

    public DateOnly Today => Domain.AnalyticsClock.LocalDate(now);

    public int RowCap => rowCap;

    /// <summary>True when a query returned more rows than <see cref="RowCap"/> (the extra rows are dropped).</summary>
    public bool Truncated { get; private set; }

    /// <summary>Runs a query, returning at most <see cref="RowCap"/> rows.</summary>
    public async Task<List<T>> ListAsync<T>(IQueryable<T> query, CancellationToken ct)
    {
        var rows = await executor.ListAsync(query.Take(rowCap + 1), ct);
        if (rows.Count > rowCap)
        {
            rows.RemoveAt(rows.Count - 1);
            Truncated = true;
        }

        return rows;
    }

    /// <summary>Runs a single-row aggregate (a GROUP BY on a constant) and returns its row, or null when there is no data.</summary>
    public async Task<T?> SingleAsync<T>(IQueryable<T> query, CancellationToken ct)
        where T : class =>
        (await executor.ListAsync(query.Take(1), ct)).FirstOrDefault();

    /// <summary>
    /// Runs a query whose rows are measurements summarised in memory (turnaround times, stays); allows more rows than
    /// <see cref="RowCap"/> and flags truncation beyond <paramref name="cap"/>.
    /// </summary>
    public async Task<List<T>> MeasurementsAsync<T>(IQueryable<T> query, CancellationToken ct, int cap = MeasurementCap)
    {
        var rows = await executor.ListAsync(query.Take(cap + 1), ct);
        if (rows.Count > cap)
        {
            rows.RemoveAt(rows.Count - 1);
            Truncated = true;
        }

        return rows;
    }

    /// <summary>Master-data look-ups (names for grouped ids); bounded by the number of rows already returned.</summary>
    public Task<List<T>> LookupAsync<T>(IQueryable<T> query, CancellationToken ct) => executor.ListAsync(query.Take(LookupCap), ct);

    /// <summary>Most measurement rows read for in-memory statistics (about 10 MB of small tuples).</summary>
    public const int MeasurementCap = 250_000;

    private const int LookupCap = 20_000;
}
