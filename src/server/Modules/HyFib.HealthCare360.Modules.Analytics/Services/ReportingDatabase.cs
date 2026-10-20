using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Optional read-only reporting database (SCAL-03, BI-03). When <c>ConnectionStrings:Analytics</c> (or the older name
/// <c>ConnectionStrings:Reporting</c>) points at a PostgreSQL streaming replica, every report, KPI and report look-up query
/// runs there so heavy MIS work never competes with the wards and counters for the primary database. Without it, reports
/// run on the primary database through a separate, read-only context. When the replica cannot be reached, the primary
/// database answers instead, and the result says so (<see cref="ReportingDbContextFactory.RunAsync{T}"/>).
/// </summary>
internal sealed class ReportingDatabase
{
    public const string ConnectionName = "Analytics";
    public const string LegacyConnectionName = "Reporting";

    public ReportingDatabase(IConfiguration configuration, DatabaseCredentials credentials)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName) ?? configuration.GetConnectionString(LegacyConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        // A replica signing in as the server's own account follows its password when Administration changes it: the
        // change reaches the replica with the rest of the database.
        var (replica, supplyPassword) = credentials.For(connectionString);

        // Built once: EF Core caches the internal services and the model per options instance.
        Options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(replica, npgsql => npgsql.CommandTimeout(120).ConfigureDataSource(supplyPassword))
            .UseSnakeCaseNamingConvention()
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
    }

    /// <summary>Options for the replica, or null when reports use the primary database.</summary>
    public DbContextOptions<HospitalDbContext>? Options { get; }

    public bool UsesReplica => Options is not null;

    public const string ReplicaLabel = "Reporting replica";
    public const string LiveLabel = "Live database";

    /// <summary>Where the figures came from when the replica could not be reached and the primary database answered.</summary>
    public const string FallbackLabel = "Live database (reporting replica unavailable)";

    public string SourceLabel => UsesReplica ? ReplicaLabel : LiveLabel;
}

/// <summary>What reporting work returned, and which database answered it (<see cref="ReportingDatabase.SourceLabel"/>).</summary>
public sealed record ReportingResult<T>(T Value, string DataSource);

/// <summary>
/// Creates the <see cref="HospitalDbContext"/> a report runs on. The context carries a <see cref="ReportingUser"/> whose
/// facility is the one the report is for, so the platform's facility query filter scopes every facility-owned table
/// (and a null facility — a consolidated report — disables it, exactly as for system jobs).
/// </summary>
internal sealed class ReportingDbContextFactory(
    IServiceProvider services, ModuleCatalog modules, ReportingDatabase reporting, ILogger<ReportingDbContextFactory> logger)
{
    public HospitalDbContext Create(Guid? facilityId)
    {
        var options = reporting.Options ?? services.GetRequiredService<DbContextOptions<HospitalDbContext>>();
        return new HospitalDbContext(options, modules, new ReportingUser(facilityId));
    }

    /// <summary>A reporting context on the primary database (fallback when the replica is unreachable).</summary>
    public HospitalDbContext CreatePrimary(Guid? facilityId) =>
        new(services.GetRequiredService<DbContextOptions<HospitalDbContext>>(), modules, new ReportingUser(facilityId));

    /// <summary>
    /// Runs read-only reporting work on the reporting database. When that is a replica and it cannot be reached
    /// (maintenance, the network), or goes away during the work (a restart), the same work runs again on the primary
    /// database, so a report, the KPIs or a look-up never fail because the replica is down (SCAL-03). A query the replica
    /// answered with an error, or one that ran past its time there, is not run again: the heavy reports the replica is
    /// for must not move to the wards' database, and the user is told instead. The work must only read: it may run twice.
    /// </summary>
    /// <param name="what">What is read, for the warning logged when the primary database has to answer.</param>
    public async Task<ReportingResult<T>> RunAsync<T>(Guid? facilityId, string what, Func<HospitalDbContext, Task<T>> work, CancellationToken ct)
    {
        await using var db = Create(facilityId);
        try
        {
            // Opened first, so that a timeout means the replica could not be reached, not that the query took too long.
            await db.Database.OpenConnectionAsync(ct);
        }
        catch (Exception ex) when (reporting.UsesReplica && !ct.IsCancellationRequested && IsUnreachable(ex, connecting: true))
        {
            return await OnPrimaryAsync(facilityId, what, work, ex);
        }

        try
        {
            return new ReportingResult<T>(await work(db), reporting.SourceLabel);
        }
        catch (Exception ex) when (reporting.UsesReplica && !ct.IsCancellationRequested && IsUnreachable(ex, connecting: false))
        {
            return await OnPrimaryAsync(facilityId, what, work, ex);
        }
    }

    /// <summary>
    /// The replica could not be reached (<paramref name="connecting"/>: refused, unresolved, or no answer in time), or the
    /// connection to it broke or was shut down during the work, so the primary database is worth asking instead. Not an
    /// error the replica answered with (a missing table, a query its own recovery cancelled), and not a query that ran
    /// past its time on a replica that answered.
    /// </summary>
    internal static bool IsUnreachable(Exception ex, bool connecting) => ex switch
    {
        // 08: connection exceptions; 57P01-57P03: the server is shutting down, crashed, or cannot take connections yet.
        PostgresException postgres => postgres.SqlState.StartsWith("08", StringComparison.Ordinal) || postgres.SqlState is "57P01" or "57P02" or "57P03",
        NpgsqlException { InnerException: TimeoutException } => connecting,
        NpgsqlException npgsql => npgsql.IsTransient,
        TimeoutException => connecting,
        InvalidOperationException { InnerException: { } inner } => IsUnreachable(inner, connecting),
        _ => false,
    };

    private async Task<ReportingResult<T>> OnPrimaryAsync<T>(Guid? facilityId, string what, Func<HospitalDbContext, Task<T>> work, Exception ex)
    {
        logger.LogWarning(ex, "Reporting replica unavailable; {What} falls back to the primary database", what);
        await using var primary = CreatePrimary(facilityId);
        return new ReportingResult<T>(await work(primary), ReportingDatabase.FallbackLabel);
    }
}

/// <summary>The identity a reporting context runs as: only the facility matters (it drives the facility filter).</summary>
internal sealed class ReportingUser(Guid? facilityId) : ICurrentUser
{
    public bool IsAuthenticated => false;

    public Guid? UserId => null;

    public string? UserName => null;

    public string? DisplayName => null;

    public Guid? FacilityId => facilityId;

    public Guid? SessionId => null;

    public Guid? DeviceId => null;

    public Guid? PractitionerId => null;

    public Guid? PatientId => null;

    public bool IsPatient => false;

    public IReadOnlyCollection<string> Roles => [];

    public string? IpAddress => null;

    public string? CorrelationId => null;
}
