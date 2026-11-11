using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// An older version of HC360 does not start on a database a newer version changed: it would run against tables it does not
/// expect (docs/deployment/MODULE-ROLLOUT-PLAN.md, item 7). Going back from an upgrade means restoring the backup taken
/// before it first, never starting the previous version on the upgraded database (docs/deployment/DEPLOYMENT.md, section 7).
/// </summary>
public sealed class NewerDatabaseTests(HospitalServer server)
{
    /// <summary>A migration this build does not have, as a newer version would have applied it.</summary>
    private const string NewerMigration = "29991231235959_FromANewerVersion";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_older_version_does_not_start_on_a_database_a_newer_version_changed(bool autoMigrate)
    {
        await using var database = await NewDatabaseAsync();
        await StartAsync(database.ConnectionString, autoMigrate);

        await using (var db = Context(database.ConnectionString))
        {
            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(NewerMigration, "99.0.0")), Ct);
        }

        var stopped = await Should.ThrowAsync<DatabaseUpgradeException>(() => StartAsync(database.ConnectionString, autoMigrate));

        stopped.Message.ShouldBe(string.Format(CultureInfo.InvariantCulture, DatabaseInitializer.NewerDatabaseMessage, NewerMigration));
        ServerComposition.IsTransientDatabaseError(stopped).ShouldBeFalse("trying again cannot make the database older");
    }

    /// <summary>A new database with this version's tables, as a first start leaves it.</summary>
    private async Task<TestDatabase> NewDatabaseAsync()
    {
        var database = await TestDatabase.CreateAsync(Ct);
        try
        {
            await using var db = Context(database.ConnectionString);
            await db.Database.MigrateAsync(Ct);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>What a node's start does to the database (<see cref="DatabaseInitializer.InitializeAsync"/>), without seeders.</summary>
    private async Task StartAsync(string connectionString, bool autoMigrate)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Context(connectionString));
        await using var provider = services.BuildServiceProvider();
        await new DatabaseInitializer(provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new PlatformOptions { AutoMigrate = autoMigrate }), NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(Ct);
    }

    private HospitalDbContext Context(string connectionString) => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(ServerComposition).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable(MigrationBaseline.HistoryTable, MigrationBaseline.HistorySchema);
                npgsql.CommandTimeout(300);
            })
            .UseSnakeCaseNamingConvention()
            .Options,
        server.Services.GetRequiredService<ModuleCatalog>());
}
