using HyFib.HealthCare360.Modules.Patients.Seed;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Audit P11: demonstration data (<see cref="PlatformOptions.SeedDemoData"/>) goes only into a database made for training or
/// testing (<see cref="TrainingDatabase"/>). Turned on for a hospital's live database, which holds patients, it would mix
/// demonstration patients, wards, stock, employees and accounts that share one password into real records, and let the
/// integrations use their simulators; so the server does not start, and the load test adds no synthetic patients or accounts.
/// A database installed as for a hospital becomes a training one when demonstration data is turned on before anyone is
/// registered (docs/testing/LOAD-TESTS.md), and stays one.
/// </summary>
public sealed class TrainingDatabaseTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_server_with_demonstration_data_does_not_start_on_a_database_that_holds_patients()
    {
        await using var database = await NewDatabaseAsync();
        await StartAsync(database.ConnectionString, seedDemoData: false);
        await RegisterPatientAsync(database.ConnectionString, "IT-LIVE-1");

        var stopped = await Should.ThrowAsync<DatabaseUpgradeException>(() => StartAsync(database.ConnectionString, seedDemoData: true));

        stopped.Message.ShouldBe(TrainingDatabase.LiveDatabaseMessage);
        ServerComposition.IsTransientDatabaseError(stopped).ShouldBeFalse();
        await using var db = Context(database.ConnectionString);
        (await TrainingDatabase.IsTrainingAsync(db, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_database_with_no_patients_when_demonstration_data_is_turned_on_stays_a_training_database()
    {
        await using var database = await NewDatabaseAsync();
        await StartAsync(database.ConnectionString, seedDemoData: false);
        await StartAsync(database.ConnectionString, seedDemoData: true);
        await RegisterPatientAsync(database.ConnectionString, "IT-TRAIN-1");

        await StartAsync(database.ConnectionString, seedDemoData: true);

        await using var db = Context(database.ConnectionString);
        (await TrainingDatabase.IsTrainingAsync(db, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_load_test_adds_no_synthetic_patients_or_accounts_to_a_database_not_made_for_training()
    {
        await SetTrainingAsync(false);
        try
        {
            await using var scope = server.Services.CreateAsyncScope();
            var patients = await scope.ServiceProvider.GetRequiredService<LoadTestPatients>().AddAsync(1, null, Ct);
            var accounts = await scope.ServiceProvider.GetRequiredService<LoadTestAccounts>().EnsureAsync(1, "load-test-password-1", Ct);

            patients.Error.ShouldNotBeNull("no synthetic patient is added").Code.ShouldBe("loadtest.not_demo");
            accounts.Error.ShouldNotBeNull("no load test account is made").Code.ShouldBe("loadtest.not_demo");
        }
        finally
        {
            await SetTrainingAsync(true);
        }
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
    private async Task StartAsync(string connectionString, bool seedDemoData)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Context(connectionString));
        await using var provider = services.BuildServiceProvider();
        await new DatabaseInitializer(provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new PlatformOptions { SeedDemoData = seedDemoData }), NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(Ct);
    }

    private static async Task RegisterPatientAsync(string connectionString, string uhid)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO patients.patients (id, uhid, first_name, full_name, gender, is_date_of_birth_estimated, blood_group, marital_status,
                category, registered_facility_id, registered_at, is_vip, is_deceased, is_active, created_at, is_deleted)
            VALUES ('{Guid.CreateVersion7()}', '{uhid}', 'Training', 'Training Test', 'Female', false, 'Unknown', 'Unknown', 'General',
                '{Guid.CreateVersion7()}', now(), false, false, true, now(), false)
            """, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Marks the run's database as made for training, as its server did when it first started, or takes the mark away.</summary>
    private async Task SetTrainingAsync(bool training)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Set<SystemSetting>().IgnoreQueryFilters().Where(s => s.Key == TrainingDatabase.SettingKey).ExecuteDeleteAsync(Ct);
        if (training)
        {
            db.Add(new SystemSetting { Key = TrainingDatabase.SettingKey, Value = "true" });
            await db.SaveChangesAsync(Ct);
        }
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
