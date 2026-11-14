using System.Reflection;
using HyFib.HealthCare360.ArchitectureTests;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Server;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// An upgrade on a database that holds a hospital's records (SRS MNT-03), as every upgrade after go-live is. A server of the
/// test's own builds its database with the real migrations and fills it with the code base's own seeders: the demonstration
/// patients, visits, queue, stock, staff and settings the shared server has. It then steps the database back to an earlier
/// migration with each newer migration's own Down: to the previous release's newest migration (MigrationPolicy.Releases),
/// or, until a release is published or when none is newer, to the migration <see cref="MigrationsBack"/> back. The seeders
/// write this version's tables, so the rows are made first and the database is stepped back after, which leaves it as that
/// migration has it, with the rows in it. The server's own start then upgrades it, as it upgrades a hospital's
/// (DatabaseInitializer: the pending migrations, the audit triggers, the seeders again). No row of a table the earlier
/// migration had is lost, no number series goes back (a number would be issued twice), the history holds every migration,
/// EF Core reads every table, and the server works on it: staff sign in, find a patient, see today's queue, register a new
/// patient, and the audit chain verifies.
/// </summary>
public sealed class UpgradeTests
{
    /// <summary>How many migrations back the upgrade starts while no newer release is published: the newest are the least tried.</summary>
    private const int MigrationsBack = 10;

    /// <summary>Where the test keeps, during the upgrade, the keys of every row the earlier migration's tables held.</summary>
    private const string Kept = "upgrade_check";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_populated_database_upgraded_from_an_earlier_migration_keeps_every_row_and_the_server_works_on_it()
    {
        Upgrade? upgrade = null;
        await using var hospital = new HospitalServer(beforeStart: async (services, ct) => upgrade = await StepBackAndUpgradeAsync(services, ct));
        await hospital.InitializeAsync();

        var done = upgrade.ShouldNotBeNull();
        done.Upgraded.ShouldNotBeEmpty();
        foreach (var table in new[] { "patients.patients", "patients.visits", "patients.queue_tokens", "identity.users", "platform.number_series" })
        {
            done.Rows.GetValueOrDefault(table).ShouldBeGreaterThan(0, $"the seed put rows in {table} before the upgrade from {done.Baseline}");
        }

        done.Lost.ShouldBeEmpty($"no row is lost upgrading from {done.Baseline} through {string.Join(", ", done.Upgraded)}");
        done.CountersBack.ShouldBeEmpty("no number series goes back, or it would issue a number again");

        await using (var scope = hospital.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Database.GetAppliedMigrationsAsync(Ct)).ShouldBe(db.Database.GetMigrations());
            (await UnreadableAsync(db)).ShouldBeEmpty("EF Core reads every table of the upgraded database");
        }

        // The server works on it: the front desk finds the last patient registered before the upgrade, sees today's queue,
        // and registers a new patient, whose UHID the series issues after every UHID issued before; the audit chain verifies.
        var frontOffice = await hospital.SignInAsync(Users.FrontOffice);
        var (patientId, uhid) = await LastPatientAsync(hospital);
        (await frontOffice.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={uhid}&pageSize=5")).Items.ShouldContain(p => p.Id == patientId);
        var generalMedicine = (await frontOffice.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments")).Single(d => d.Code == "GM");
        (await frontOffice.GetAsync<List<QueueEntry>>($"/api/v1/queue?departmentId={generalMedicine.Id}")).ShouldNotBeEmpty("the seed's tokens wait in today's queue");
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", NewPatient());
        string.CompareOrdinal(registered.Uhid, uhid).ShouldBeGreaterThan(0);
        var admin = await hospital.SignInAsync(Users.HospitalAdmin);
        (await admin.GetAsync<AuditVerification>("/api/v1/admin/audit/verify")).Valid.ShouldBeTrue("the audit chain verifies after the upgrade");
    }

    /// <summary>
    /// Steps the seeded database back to the earlier migration, keeps the keys of every row its tables hold and the number
    /// series' counters, has the server bring it up to date as its start does, and says what the upgrade lost.
    /// </summary>
    private static async Task<Upgrade> StepBackAndUpgradeAsync(IServiceProvider services, CancellationToken ct)
    {
        string baseline;
        List<string> upgraded;
        HashSet<string> takenAway;
        string[] schemas;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var migrations = db.Database.GetMigrations().ToList();
            baseline = Baseline(migrations);
            upgraded = migrations[(migrations.IndexOf(baseline) + 1)..];
            takenAway = TablesTakenAway(db, upgraded);
            schemas = [.. db.Model.GetEntityTypes().Select(e => e.GetSchema() ?? "public").Distinct()];
            await db.GetService<IMigrator>().MigrateAsync(baseline, ct);
        }

        await using var connection = new NpgsqlConnection(services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital"));
        await connection.OpenAsync(ct);
        var tables = await KeyedTablesAsync(connection, schemas, ct);
        await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {Kept} CASCADE; CREATE SCHEMA {Kept}", ct);
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < tables.Count; i++)
        {
            await ExecuteAsync(connection, $"CREATE TABLE {Kept}.t{i} AS SELECT {string.Join(", ", tables[i].Keys.Select(Quote))} FROM {tables[i].Sql}", ct);
            rows[tables[i].Name] = await ScalarAsync<long>(connection, $"SELECT count(*) FROM {Kept}.t{i}", ct);
        }

        var counters = await CountersAsync(connection, ct);

        // As the server's start on a hospital's server after the new version is installed.
        await ServerComposition.InitializeDatabaseAsync(services, ct);

        var lost = new List<string>();
        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            if (await ScalarAsync<bool>(connection, $"SELECT to_regclass('{table.Sql}') IS NULL", ct))
            {
                if (!takenAway.Contains(table.Name))
                {
                    lost.Add($"{table.Name}: the table is gone");
                }

                continue;
            }

            var same = string.Join(" AND ", table.Keys.Select(key => $"t.{Quote(key)} = k.{Quote(key)}"));
            var missing = await ScalarAsync<long>(connection, $"SELECT count(*) FROM {Kept}.t{i} k WHERE NOT EXISTS (SELECT 1 FROM {table.Sql} t WHERE {same})", ct);
            if (missing > 0)
            {
                lost.Add($"{table.Name}: {missing} of {rows[table.Name]} rows");
            }
        }

        var after = await CountersAsync(connection, ct);
        var back = counters
            .Where(before => after.TryGetValue(before.Key, out var now) && (string.CompareOrdinal(now.Period, before.Value.Period) < 0
                || (now.Period == before.Value.Period && now.Value < before.Value.Value)))
            .Select(before => $"{before.Key}: {before.Value} to {after[before.Key]}")
            .ToList();
        await ExecuteAsync(connection, $"DROP SCHEMA {Kept} CASCADE", ct);
        return new Upgrade(baseline, upgraded, rows, lost, back);
    }

    /// <summary>
    /// The migration the upgrade starts from: the previous release's newest, when a newer migration follows it; otherwise the
    /// one <see cref="MigrationsBack"/> back.
    /// </summary>
    private static string Baseline(List<string> migrations)
    {
        var released = MigrationPolicy.Releases.Count > 0 ? migrations.IndexOf(MigrationPolicy.Releases[^1].LastMigration) : -1;
        return released >= 0 && released < migrations.Count - 1 ? migrations[released] : migrations[^(MigrationsBack + 1)];
    }

    /// <summary>The tables the upgrade's migrations drop or rename on purpose (reviewed contract steps): not lost, gone.</summary>
    private static HashSet<string> TablesTakenAway(HospitalDbContext db, List<string> upgraded)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in upgraded)
        {
            foreach (var operation in assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!).UpOperations)
            {
                switch (operation)
                {
                    case DropTableOperation drop:
                        gone.Add($"{drop.Schema ?? "public"}.{drop.Name}");
                        break;
                    case RenameTableOperation rename:
                        gone.Add($"{rename.Schema ?? "public"}.{rename.Name}");
                        break;
                }
            }
        }

        return gone;
    }

    /// <summary>The application's tables in <paramref name="schemas"/> (partitions aside), each with its primary key's columns.</summary>
    private static async Task<List<KeyedTable>> KeyedTablesAsync(NpgsqlConnection connection, string[] schemas, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, array_agg(a.attname::text ORDER BY k.ord)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
            WHERE con.contype = 'p' AND c.relkind IN ('r', 'p') AND NOT c.relispartition
              AND n.nspname = ANY(@schemas) AND c.relname <> @history
            GROUP BY n.nspname, c.relname
            ORDER BY 1, 2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas });
        command.Parameters.AddWithValue("history", MigrationBaseline.HistoryTable);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var tables = new List<KeyedTable>();
        while (await reader.ReadAsync(ct))
        {
            tables.Add(new KeyedTable(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2)));
        }

        return tables;
    }

    /// <summary>Each number series' counter: by its key and facility, the last number issued and the period it counts in.</summary>
    private static async Task<Dictionary<string, (long Value, string Period)>> CountersAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT key || '/' || coalesce(facility_id::text, 'shared'), current_value, coalesce(current_period, '') FROM platform.number_series", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var counters = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            counters[reader.GetString(0)] = (reader.GetInt64(1), reader.GetString(2));
        }

        return counters;
    }

    /// <summary>The tables EF Core cannot read from the database: a column or a table its model has and the database lacks, a value it cannot take.</summary>
    private static async Task<List<string>> UnreadableAsync(HospitalDbContext db)
    {
        var read = typeof(UpgradeTests).GetMethod(nameof(ReadOneAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
        var unreadable = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes().Where(e => e.BaseType is null && !e.IsOwned() && !e.HasSharedClrType && e.GetTableName() is not null))
        {
            try
            {
                await (Task)read.MakeGenericMethod(entity.ClrType).Invoke(null, [db])!;
            }
            catch (PostgresException ex)
            {
                unreadable.Add($"{entity.GetSchema()}.{entity.GetTableName()}: {ex.MessageText}");
            }
            catch (InvalidOperationException ex)
            {
                unreadable.Add($"{entity.GetSchema()}.{entity.GetTableName()}: {ex.Message}");
            }
        }

        return unreadable;
    }

    private static Task ReadOneAsync<T>(HospitalDbContext db)
        where T : class => db.Set<T>().IgnoreQueryFilters().AsNoTracking().Take(1).ToListAsync(Ct);

    /// <summary>
    /// The last patient registered before the upgrade, whom the front desk's search lists: the highest UHID, which the series
    /// issued last, of the active records.
    /// </summary>
    private static async Task<(Guid Id, string Uhid)> LastPatientAsync(HospitalServer hospital)
    {
        await using var scope = hospital.Services.CreateAsyncScope();
        var patient = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().AsNoTracking()
            .Where(p => p.IsActive && p.MergedIntoPatientId == null)
            .OrderByDescending(p => p.Uhid).Select(p => new { p.Id, p.Uhid }).FirstAsync(Ct);
        return (patient.Id, patient.Uhid);
    }

    private static RegisterPatientRequest NewPatient() => new(
        Title: null, FirstName: TestPatients.FirstName("Meena"), MiddleName: null, LastName: "Upgrade", Gender: Gender.Female,
        DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
        Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10)))), AlternatePhone: null, Email: null,
        Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
        EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
        Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
        ConfirmedNotDuplicate: false);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>A table and its primary key's columns; <see cref="Sql"/> names it in SQL.</summary>
    private sealed record KeyedTable(string Schema, string Table, string[] Keys)
    {
        public string Name => $"{Schema}.{Table}";

        public string Sql => $"{Quote(Schema)}.{Quote(Table)}";
    }

    /// <param name="Baseline">The migration the upgrade started from.</param>
    /// <param name="Upgraded">The migrations the upgrade applied, in order.</param>
    /// <param name="Rows">The rows each table held at the baseline, by schema and table.</param>
    /// <param name="Lost">Each table that lost rows in the upgrade, and how many.</param>
    /// <param name="CountersBack">Each number series whose counter the upgrade moved back.</param>
    private sealed record Upgrade(string Baseline, IReadOnlyList<string> Upgraded, IReadOnlyDictionary<string, long> Rows, IReadOnlyList<string> Lost, IReadOnlyList<string> CountersBack);
}
