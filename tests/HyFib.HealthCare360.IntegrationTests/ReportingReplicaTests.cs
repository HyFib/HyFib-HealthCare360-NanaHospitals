using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SCAL-03: MIS and BI queries (reports, KPIs, report look-ups) run on the reporting replica named by
/// <c>ConnectionStrings:Analytics</c>, and on the live database without one. A replica that cannot be reached does not
/// fail them: the live database answers, and the result says so. A query the replica refuses for any other reason is not
/// run again on the live database. The "replica" here is another database on the test PostgreSQL, so the answer shows
/// which one ran the query.
/// </summary>
public sealed class ReportingReplicaTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_replica_reports_read_the_live_database()
    {
        var (database, source) = await WhichDatabaseAsync(replica: null);

        (database, source).ShouldBe((LiveDatabase(), ReportingDatabase.LiveLabel));
    }

    [Fact]
    public async Task With_a_replica_reports_read_it()
    {
        await using var replica = await TestDatabase.CreateAsync(Ct);

        var (database, source) = await WhichDatabaseAsync(replica.ConnectionString);

        (database, source).ShouldBe((new NpgsqlConnectionStringBuilder(replica.ConnectionString).Database, ReportingDatabase.ReplicaLabel));
    }

    [Fact]
    public async Task When_the_replica_cannot_be_reached_the_live_database_answers_and_says_so()
    {
        // Nothing listens on port 9 of this machine.
        var (database, source) = await WhichDatabaseAsync("Host=127.0.0.1;Port=9;Database=hc360;Username=hc360;Password=unused;Timeout=3");

        (database, source).ShouldBe((LiveDatabase(), ReportingDatabase.FallbackLabel));
    }

    [Fact]
    public async Task A_query_the_replica_refuses_is_not_run_again_on_the_live_database()
    {
        await using var replica = await TestDatabase.CreateAsync(Ct);
        await using var scope = server.Services.CreateAsyncScope();
        var contexts = Factory(scope.ServiceProvider, replica.ConnectionString);
        var runs = 0;

        // The replica has no HC360 tables: the query fails there, which says nothing about whether it can be reached.
        await Should.ThrowAsync<PostgresException>(() => contexts.RunAsync(null, "an IT query", db =>
        {
            runs++;
            return db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM platform.system_settings").SingleAsync(Ct);
        }, Ct));
        runs.ShouldBe(1);
    }

    private async Task<(string Database, string Source)> WhichDatabaseAsync(string? replica)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var result = await Factory(scope.ServiceProvider, replica).RunAsync(null, "an IT query",
            db => db.Database.SqlQuery<string>($"SELECT current_database() AS \"Value\"").SingleAsync(Ct), Ct);
        return (result.Value, result.DataSource);
    }

    /// <summary>The reporting contexts as the Analytics module makes them, with <paramref name="replica"/> as the reporting database.</summary>
    private static ReportingDbContextFactory Factory(IServiceProvider scope, string? replica)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{ReportingDatabase.ConnectionName}"] = replica })
            .Build();
        var reporting = new ReportingDatabase(configuration, scope.GetRequiredService<DatabaseCredentials>());
        return new ReportingDbContextFactory(scope, scope.GetRequiredService<ModuleCatalog>(), reporting, NullLogger<ReportingDbContextFactory>.Instance);
    }

    private string LiveDatabase() =>
        new NpgsqlConnectionStringBuilder(server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital")).Database!;
}
