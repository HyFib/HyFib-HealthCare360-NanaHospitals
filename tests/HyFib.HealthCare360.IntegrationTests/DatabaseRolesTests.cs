using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The two database accounts (docs/deployment/DEPLOYMENT.md, "Two database accounts") on a real PostgreSQL, set up by the
/// installer's own SQL (deploy/windows/hc360-database-roles.sql) on a database of their own. A database installed before the
/// split, which the working account owns, is handed to the owner; start-up then applies the pending migration and the audit
/// trail's triggers as the owner (<see cref="DatabaseOwner"/>), and the working account can read and write rows but not
/// change a table, drop or turn off a trigger, or empty the audit trail.
/// </summary>
public sealed class DatabaseRolesTests(HospitalServer server)
{
    private const string Password = "roles-Test-Password-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_working_account_writes_rows_but_cannot_change_the_audit_trail_or_its_triggers()
    {
        var database = await TestDatabase.CreateAsync(Ct);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var owner = "hc360_owner_" + suffix;
        var app = "hc360_app_" + suffix;
        await ExecuteAsync(database.ConnectionString, $"CREATE ROLE {owner} LOGIN PASSWORD '{Password}'; CREATE ROLE {app} LOGIN PASSWORD '{Password}'");
        try
        {
            var asOwner = As(database.ConnectionString, owner);
            var asApp = As(database.ConnectionString, app);
            var databaseName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database;

            // Installed before the split: the working account owns the database and made every table, up to the last
            // migration but one.
            await ExecuteAsync(database.ConnectionString,
                $"CREATE EXTENSION IF NOT EXISTS pg_trgm; ALTER DATABASE \"{databaseName}\" OWNER TO {app}; GRANT CREATE ON SCHEMA public TO {app}");
            string lastMigration;
            await using (var db = Context(asApp))
            {
                var migrations = db.Database.GetMigrations().ToList();
                lastMigration = migrations[^1];
                await db.GetService<IMigrator>().MigrateAsync(migrations[^2], Ct);
            }

            // The installer splits the accounts.
            await RunRolesSqlAsync(database.ConnectionString, owner, app);
            (await ScalarAsync<long>(database.ConnectionString, $"""
                SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE pg_get_userbyid(c.relowner) = '{app}' AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
                """)).ShouldBe(0, "every table, sequence and index the working account made is the owner's now");

            // Start-up, as the server's: the last migration and the triggers as the owner, the seeders as the working account.
            await StartNodeAsync(asApp, asOwner);
            (await ScalarAsync<bool>(asApp, $"SELECT EXISTS (SELECT 1 FROM \"{MigrationBaseline.HistorySchema}\".\"{MigrationBaseline.HistoryTable}\" WHERE migration_id = '{lastMigration}')"))
                .ShouldBeTrue("the pending migration was applied, as the owner: the working account can no longer change a table");
            (await ScalarAsync<string>(database.ConnectionString,
                    "SELECT pg_get_userbyid(p.proowner) FROM pg_proc p WHERE p.oid = 'audit.drop_purged_month(regclass)'::regprocedure"))
                .ShouldBe(owner, "start-up made the audit trail's functions as the owner");
            (await ScalarAsync<long>(database.ConnectionString, $"""
                SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE pg_get_userbyid(c.relowner) <> '{owner}' AND c.relkind IN ('r', 'p') AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
                """)).ShouldBe(0, "the tables the last migration made are the owner's too");

            // What the server does: write and read rows, the audit trail's included.
            await ExecuteAsync(asApp, "INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES (now(), 'security', 'it.roles', 'Info')");
            (await ScalarAsync<long>(asApp, "SELECT count(*) FROM audit.audit_events WHERE action = 'it.roles'")).ShouldBe(1);

            // What it must not: change the audit trail's table or triggers, empty it, or become the owner.
            (await RefusalAsync(asApp, "ALTER TABLE audit.audit_events DISABLE TRIGGER trg_protect_audit_events")).ShouldNotBeNull().ShouldContain("must be owner");
            (await RefusalAsync(asApp, "ALTER TABLE audit.audit_events DISABLE TRIGGER ALL")).ShouldNotBeNull().ShouldContain("must be owner");
            (await RefusalAsync(asApp, "DROP TRIGGER trg_protect_audit_events ON audit.audit_events")).ShouldNotBeNull().ShouldContain("must be owner");
            (await RefusalAsync(asApp, "DROP TRIGGER trg_check_audit_chain_link ON audit.audit_events")).ShouldNotBeNull().ShouldContain("must be owner");
            (await RefusalAsync(asApp, "ALTER TABLE audit.audit_events ADD COLUMN note text")).ShouldNotBeNull().ShouldContain("must be owner");
            (await RefusalAsync(asApp, "DROP TABLE audit.audit_events")).ShouldNotBeNull().ShouldContain("must be owner");
            (await RefusalAsync(asApp, "CREATE OR REPLACE FUNCTION audit.protect_audit_events() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$"))
                .ShouldNotBeNull().ShouldContain("permission denied for schema audit", customMessage: "it may not even make a function there");
            (await RefusalAsync(asApp, "TRUNCATE audit.audit_events")).ShouldNotBeNull().ShouldContain("permission denied");
            (await RefusalAsync(asApp, "DELETE FROM audit.audit_events")).ShouldNotBeNull().ShouldContain("append-only");
            (await RefusalAsync(asApp, $"SET ROLE {owner}")).ShouldNotBeNull().ShouldContain("permission denied");
            (await RefusalAsync(asApp, "CREATE TABLE public.it_roles (id int)")).ShouldNotBeNull().ShouldContain("permission denied");

            // The one table it may drop, through the purge's function: a month of the audit trail, and nothing else.
            (await RefusalAsync(asApp, "SELECT audit.drop_purged_month('audit.audit_purges'::regclass)")).ShouldNotBeNull().ShouldContain("not a month of the audit trail");

            // Running the installer's SQL again changes nothing.
            await RunRolesSqlAsync(database.ConnectionString, owner, app);
            (await RefusalAsync(asApp, "ALTER TABLE audit.audit_events DISABLE TRIGGER trg_protect_audit_events")).ShouldNotBeNull().ShouldContain("must be owner");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await database.DisposeAsync();
            await ExecuteAsync(database.ConnectionString.Replace(new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!, "postgres", StringComparison.Ordinal),
                $"DROP ROLE IF EXISTS {app}; DROP ROLE IF EXISTS {owner}", ignoreErrors: true);
        }
    }

    /// <summary>The installer's SQL, as it runs it: the two accounts named in session settings, then the file.</summary>
    private static async Task RunRolesSqlAsync(string superuser, string owner, string app)
    {
        var sql = await File.ReadAllTextAsync(RolesSqlPath(), Ct);
        await using var connection = new NpgsqlConnection(superuser);
        await connection.OpenAsync(Ct);
        await using (var names = new NpgsqlCommand("SELECT set_config('hc360.owner_role', @owner, false), set_config('hc360.app_role', @app, false)", connection))
        {
            names.Parameters.AddWithValue("owner", owner);
            names.Parameters.AddWithValue("app", app);
            await names.ExecuteNonQueryAsync(Ct);
        }

        await using var roles = new NpgsqlCommand(sql, connection);
        await roles.ExecuteNonQueryAsync(Ct);
    }

    private static string RolesSqlPath()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "deploy", "windows", "hc360-database-roles.sql");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("deploy/windows/hc360-database-roles.sql was not found above the test's folder.");
    }

    /// <summary>What a node's start does to the database, with the owner apart (<see cref="DatabaseInitializer.InitializeAsync"/>), without seeders.</summary>
    private async Task StartNodeAsync(string app, string owner)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Context(app));
        services.AddSingleton(new DatabaseOwner(Options(owner), server.Services.GetRequiredService<ModuleCatalog>()));
        services.AddSingleton<IPreMigrationBackup, NoBackups>();
        await using var provider = services.BuildServiceProvider();
        var platform = new PlatformOptions();
        platform.Backup.BeforeMigrations = false;
        await new DatabaseInitializer(provider.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(platform), NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(Ct);
    }

    private static string As(string connectionString, string role) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = Password, Pooling = false }.ConnectionString;

    private static DbContextOptions<HospitalDbContext> Options(string connectionString) => new DbContextOptionsBuilder<HospitalDbContext>()
        .UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsAssembly(typeof(ServerComposition).Assembly.GetName().Name);
            npgsql.MigrationsHistoryTable(MigrationBaseline.HistoryTable, MigrationBaseline.HistorySchema);
            npgsql.CommandTimeout(300);
        })
        .UseSnakeCaseNamingConvention()
        .Options;

    private HospitalDbContext Context(string connectionString) => new(Options(connectionString), server.Services.GetRequiredService<ModuleCatalog>());

    /// <summary>Why PostgreSQL refused <paramref name="sql"/>, or null when it ran it. Rolled back either way.</summary>
    private static async Task<string?> RefusalAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(Ct);
            return null;
        }
        catch (PostgresException refused)
        {
            return refused.MessageText;
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql, bool ignoreErrors = false)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (NpgsqlException) when (ignoreErrors)
        {
        }
    }

    /// <summary>Start-up here takes no backup before the update (Backup:BeforeMigrations off).</summary>
    private sealed class NoBackups : IPreMigrationBackup
    {
        public Task<BackupResult> CreatePreMigrationAsync(IReadOnlyList<string> appliedMigrations, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No backup is taken here.");

        public Task RecordAsync(BackupResult result, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
