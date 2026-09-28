using System.Diagnostics;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.Modules.Analytics.Reports;

/// <summary>One run of a report (all rows, before sorting and paging).</summary>
public sealed record ReportExecution(
    ReportDescriptor Report,
    ReportArguments Arguments,
    FacilityScope Facility,
    ReportData Data,
    IReadOnlyList<ReportColumn> Columns,
    DateTimeOffset GeneratedAt,
    string DataSource);

/// <summary>
/// Runs catalogue reports for an actor: permission check, parameter validation, facility resolution, execution on the
/// reporting context (replica when configured) and a one-minute cache of aggregate results so dashboards, paging and
/// exports of the same report do not re-query. Reports that list patients are never cached.
/// </summary>
internal sealed class ReportRunner(
    ReportCatalog catalog,
    ReportingDbContextFactory contexts,
    ReportingDatabase reporting,
    FacilityAccess facilities,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<ReportRunner> logger)
{
    /// <summary>Most rows an aggregate report returns (flagged as truncated beyond this).</summary>
    public const int RowCap = 5_000;

    /// <summary>Most rows a patient-level register returns.</summary>
    public const int PatientRowCap = 10_000;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public ReportDefinition? Find(string reportId) => catalog.Find(reportId);

    public async Task<Result<ReportExecution>> RunAsync(ReportActor actor, string reportId, IReadOnlyDictionary<string, string?>? parameters, CancellationToken ct)
    {
        var definition = catalog.Find(reportId);
        if (definition is null)
        {
            return Error.NotFound("Report", reportId);
        }

        var report = definition.Descriptor;
        if (!ReportCatalog.CanRead(report, actor))
        {
            return Error.Forbidden("analytics.report_forbidden", "You do not have access to this report.");
        }

        var now = time.GetUtcNow();
        var bound = ReportParameterBinder.Bind(report, parameters, AnalyticsClock.LocalDate(now));
        if (bound.IsFailure)
        {
            return bound.Error!;
        }

        var arguments = bound.Value;
        var scope = await facilities.ResolveAsync(actor, arguments.Facility, ct);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var key = $"analytics:report:{report.Id}:{scope.Value.FacilityId?.ToString() ?? ReportKeys.AllFacilities}:{arguments.CacheKey()}";
        if (!report.ContainsPatientIdentifiers && cache.TryGetValue(key, out ReportExecution? cached) && cached is not null)
        {
            return cached;
        }

        var data = await ExecuteAsync(definition, arguments, scope.Value.FacilityId, now, ct);
        var execution = new ReportExecution(report, arguments, scope.Value, data, data.Columns ?? report.Columns, now, reporting.SourceLabel);
        if (!report.ContainsPatientIdentifiers)
        {
            cache.Set(key, execution, CacheDuration);
        }

        return execution;
    }

    private async Task<ReportData> ExecuteAsync(ReportDefinition definition, ReportArguments arguments, Guid? facilityId, DateTimeOffset now, CancellationToken ct)
    {
        var cap = definition.Descriptor.ContainsPatientIdentifiers ? PatientRowCap : RowCap;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await ExecuteOnAsync(contexts.Create(facilityId), definition, arguments, now, cap, ct);
        }
        catch (NpgsqlException ex) when (reporting.UsesReplica && ex.IsTransient && !ct.IsCancellationRequested)
        {
            // The replica is unreachable (maintenance, network): answer from the primary rather than failing the user.
            logger.LogWarning(ex, "Reporting replica unavailable; report {ReportId} falls back to the primary database", definition.Descriptor.Id);
            return await ExecuteOnAsync(contexts.CreatePrimary(facilityId), definition, arguments, now, cap, ct);
        }
        finally
        {
            logger.LogInformation("Report {ReportId} ran in {ElapsedMs} ms", definition.Descriptor.Id, stopwatch.ElapsedMilliseconds);
        }
    }

    private static async Task<ReportData> ExecuteOnAsync(HospitalDbContext db, ReportDefinition definition, ReportArguments arguments, DateTimeOffset now, int cap, CancellationToken ct)
    {
        await using (db)
        {
            var context = new ReportContext(db, arguments, now, EfReportQueryExecutor.Instance, cap);
            var data = await definition.RunAsync(context, ct);
            data.Truncated |= context.Truncated;
            return data;
        }
    }
}
