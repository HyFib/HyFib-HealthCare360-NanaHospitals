namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Optional read-only reporting database (SCAL-03, BI-03). When <c>ConnectionStrings:Analytics</c> (or the older name
/// <c>ConnectionStrings:Reporting</c>) points at a PostgreSQL streaming replica, every report and KPI query runs there so
/// heavy MIS work never competes with the wards and counters for the primary database. Without it, reports run on the
/// primary database through a separate, read-only context.
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

    public string SourceLabel => UsesReplica ? "Reporting replica" : "Live database";
}

/// <summary>
/// Creates the <see cref="HospitalDbContext"/> a report runs on. The context carries a <see cref="ReportingUser"/> whose
/// facility is the one the report is for, so the platform's facility query filter scopes every facility-owned table
/// (and a null facility — a consolidated report — disables it, exactly as for system jobs).
/// </summary>
internal sealed class ReportingDbContextFactory(IServiceProvider services, ModuleCatalog modules, ReportingDatabase reporting)
{
    public HospitalDbContext Create(Guid? facilityId)
    {
        var options = reporting.Options ?? services.GetRequiredService<DbContextOptions<HospitalDbContext>>();
        return new HospitalDbContext(options, modules, new ReportingUser(facilityId));
    }

    /// <summary>A reporting context on the primary database (fallback when the replica is unreachable).</summary>
    public HospitalDbContext CreatePrimary(Guid? facilityId) =>
        new(services.GetRequiredService<DbContextOptions<HospitalDbContext>>(), modules, new ReportingUser(facilityId));
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
