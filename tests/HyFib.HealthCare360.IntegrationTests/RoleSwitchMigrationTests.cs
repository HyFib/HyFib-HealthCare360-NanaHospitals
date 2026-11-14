using System.Reflection;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Server;
using HyFib.HealthCare360.Server.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The migration that lets a role be turned off (<see cref="RoleSwitch"/>), on a database of its own holding a role made
/// before it: the role is in use after the upgrade. A new database never has a role from before, so only this shows what
/// the new column gives the rows already there.
/// </summary>
public sealed class RoleSwitchMigrationTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_upgrade_leaves_every_role_in_use()
    {
        await using var database = await TestDatabase.CreateAsync(Ct);
        await using var db = Context(database.ConnectionString);
        var migrator = db.GetService<IMigrator>();
        var target = typeof(RoleSwitch).GetCustomAttribute<MigrationAttribute>()!.Id;
        var migrations = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], Ct);

        var id = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO identity.roles (id, name, description, is_system, is_deleted, created_at) VALUES (@id, 'Ward clerk', 'Made before the switch', false, false, now())", connection);
            insert.Parameters.AddWithValue("id", id);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await migrator.MigrateAsync(target, Ct);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var read = new NpgsqlCommand("SELECT is_active FROM identity.roles WHERE id = @id", connection);
            read.Parameters.AddWithValue("id", id);
            ((bool)(await read.ExecuteScalarAsync(Ct))!).ShouldBeTrue("an upgrade must not take any role out of use");
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
