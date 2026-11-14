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
/// expect (docs/deployment/MODULE-ROLLOUT-PLAN.md, item 7). Unless the hospital names the newer changes, going back to the
/// release just before, which runs on them since a release's changes take away nothing the release before uses
/// (expand/contract): then it starts beside exactly those (docs/deployment/DEPLOYMENT.md, section 7, "Going back").
/// </summary>
public sealed class NewerDatabaseTests(HospitalServer server)
{
    /// <summary>A migration this build does not have, as a newer version would have applied it.</summary>
    private const string NewerMigration = "29991231235959_FromANewerVersion";

    /// <summary>Another migration of the same newer version.</summary>
    private const string LaterMigration = "30000101000000_AlsoFromANewerVersion";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_older_version_starts_on_a_database_a_newer_version_changed_only_beside_the_changes_it_is_told_of(bool autoMigrate)
    {
        await using var database = await NewDatabaseAsync();
        await StartAsync(database.ConnectionString, autoMigrate);
        await AppliedByANewerVersionAsync(database.ConnectionString, NewerMigration);

        var stopped = await Should.ThrowAsync<DatabaseUpgradeException>(() => StartAsync(database.ConnectionString, autoMigrate));

        stopped.Message.ShouldBe(string.Format(CultureInfo.InvariantCulture, DatabaseInitializer.NewerDatabaseMessage, NewerMigration));
        ServerComposition.IsTransientDatabaseError(stopped).ShouldBeFalse("trying again cannot make the database older");

        // Going back to the release just before: told of the newer change by name, it starts beside it and leaves it applied.
        await StartAsync(database.ConnectionString, autoMigrate, accepted: [NewerMigration]);
        await using (var db = Context(database.ConnectionString))
        {
            (await db.Database.GetAppliedMigrationsAsync(Ct)).Last().ShouldBe(NewerMigration);
        }

        // A newer change it is not told of still stops it, and the message names that one alone.
        await AppliedByANewerVersionAsync(database.ConnectionString, LaterMigration);
        var refused = await Should.ThrowAsync<DatabaseUpgradeException>(() => StartAsync(database.ConnectionString, autoMigrate, accepted: [NewerMigration]));
        refused.Message.ShouldBe(string.Format(CultureInfo.InvariantCulture, DatabaseInitializer.NewerDatabaseMessage, LaterMigration));
    }

    /// <summary>Records <paramref name="migration"/> in the database's history as a newer version applying it would.</summary>
    private async Task AppliedByANewerVersionAsync(string connectionString, string migration)
    {
        await using var db = Context(connectionString);
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(migration, "99.0.0")), Ct);
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

    /// <summary>
    /// What a node's start does to the database (<see cref="DatabaseInitializer.InitializeAsync"/>), without seeders, told
    /// to start beside the newer changes <paramref name="accepted"/> names (<see cref="DatabaseOptions.AcceptNewerMigrations"/>).
    /// </summary>
    private async Task StartAsync(string connectionString, bool autoMigrate, string[]? accepted = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Context(connectionString));
        await using var provider = services.BuildServiceProvider();
        var options = new PlatformOptions { AutoMigrate = autoMigrate, Database = { AcceptNewerMigrations = accepted ?? [] } };
        await new DatabaseInitializer(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<DatabaseInitializer>.Instance)
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
