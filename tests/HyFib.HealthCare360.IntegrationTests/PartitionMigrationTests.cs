using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Notifications;
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
/// SRS SCAL-04, on a database of its own: the migration <see cref="PartitionHighVolumeTables"/> turns the audit trail,
/// remote-monitoring readings, the message log and the integration log, as they are before it with rows across many
/// months, into tables partitioned by month without losing or changing a row (the same count and the same checksum of
/// every row, in id order); each row is in its month's partition, or the default one; new rows go where their time says;
/// the identity, primary key, indexes, foreign key and audit triggers carry over; the audit chain still verifies and
/// seals; EF Core reads and writes the tables as before; and <c>Down</c> puts the plain tables back the same way.
/// </summary>
public sealed class PartitionMigrationTests(HospitalServer server)
{
    private static readonly string[] Tables =
    [
        "audit.audit_events",
        "virtualcare.rpm_readings",
        "platform.notification_logs",
        "integrations.integration_logs",
    ];

    /// <summary>
    /// Months before or after this one with rows: beyond the fifteen years back and three months ahead that get a
    /// partition of their own, rows go to the default partition.
    /// </summary>
    private static readonly int[] MonthsWithRows = [-12 * 20, -40, -13, -2, -1, 0, 1, 12 * 5];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_upgrade_that_cannot_lock_a_table_stops_and_leaves_the_tables_as_they_were()
    {
        await using var database = await TestDatabase.CreateAsync(Ct);
        await using var db = Context(database.ConnectionString);
        var migrator = db.GetService<IMigrator>();
        var target = typeof(PartitionHighVolumeTables).GetCustomAttribute<MigrationAttribute>()!.Id;
        var migrations = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], Ct);

        // Another HC360 server left running, or a reporting tool with a transaction open, reading the audit trail.
        await using var other = new NpgsqlConnection(database.ConnectionString);
        await other.OpenAsync(Ct);
        await using var reading = await other.BeginTransactionAsync(Ct);
        await ExecuteAsync(other, "LOCK TABLE audit.audit_events IN ACCESS SHARE MODE");

        // The upgrade gives up after the lock timeout, with PostgreSQL's own reason, instead of waiting for ever.
        var waited = Stopwatch.StartNew();
        var refused = await Should.ThrowAsync<Exception>(() => migrator.MigrateAsync(target, Ct));
        waited.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(1));
        (refused as PostgresException ?? refused.InnerException as PostgresException).ShouldNotBeNull().SqlState
            .ShouldBe(PostgresErrorCodes.LockNotAvailable);
        await reading.RollbackAsync(Ct);

        (await RelationKindsAsync(database.ConnectionString)).ShouldAllBe(k => k == "r");
        (await db.Database.GetAppliedMigrationsAsync(Ct)).ShouldNotContain(target);
    }

    [Fact]
    public async Task Partitioning_keeps_every_row_puts_each_in_its_month_and_can_be_undone()
    {
        await using var database = await TestDatabase.CreateAsync(Ct);
        await using var db = Context(database.ConnectionString);
        var migrator = db.GetService<IMigrator>();
        var target = typeof(PartitionHighVolumeTables).GetCustomAttribute<MigrationAttribute>()!.Id;
        var migrations = db.Database.GetMigrations().ToList();
        var previous = migrations[migrations.IndexOf(target) - 1];

        // The tables as a hospital has them before the upgrade: plain, with the audit trail's triggers (made at start-up)
        // and sealed rows across many months.
        await migrator.MigrateAsync(previous, Ct);
        await db.Database.ExecuteSqlRawAsync(PlatformSql.PostMigrationScript, Ct);
        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var expected = await SeedAsync(database.ConnectionString, thisMonth);
        (await new AuditChainService(db).SealPendingAsync(Ct)).ShouldBe(expected.Count(r => r.Table == "audit.audit_events"));
        await ExecuteAsync(database.ConnectionString,
            "INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES (now(), 'security', 'it.unsealed', 'Info')");
        var before = await ChecksumsAsync(database.ConnectionString);
        var lastAuditId = await ScalarAsync<long>(database.ConnectionString, "SELECT max(id) FROM audit.audit_events");
        before.Values.ShouldAllBe(c => c.Rows > 0);
        (await RelationKindsAsync(database.ConnectionString)).ShouldAllBe(k => k == "r");

        await migrator.MigrateAsync(target, Ct);

        // Every row is there, unchanged, and in its month's partition.
        (await ChecksumsAsync(database.ConnectionString)).ShouldBe(before);
        (await RelationKindsAsync(database.ConnectionString)).ShouldAllBe(k => k == "p");
        foreach (var row in expected)
        {
            (await PartitionOfAsync(database.ConnectionString, row.Table, row.Key, row.Id)).ShouldBe(row.Partition, $"{row.Table} row at {row.At:O}");
        }

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            foreach (var table in Tables)
            {
                // This month, the next three and the months with rows (within fifteen years), and the default partition.
                var leaves = await ScalarAsync<long>(connection, $"SELECT count(*) FROM pg_partition_tree('{table}') WHERE isleaf");
                leaves.ShouldBe(MonthsWithRows.Count(m => m is >= -12 * 15 and < 0) + 4 + 1, table);
                (await ScalarAsync<string>(connection, $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = '{table}'::regclass AND contype = 'p'"))
                    .ShouldBe($"PRIMARY KEY (id, {KeyColumn(table)})");
            }

            // The audit trail's chain position is indexed, not unique (a unique index would need the month in it), and
            // its other indexes are all there, on every partition.
            (await IndexesAsync(connection, "audit.audit_events")).ShouldBe(
            [
                "ix_audit_events_category_occurred_at",
                "ix_audit_events_chain_index",
                "ix_audit_events_occurred_at",
                "ix_audit_events_patient_id_occurred_at",
                "ix_audit_events_unsealed",
                "ix_audit_events_user_id_occurred_at",
                "pk_audit_events",
            ]);
            (await ScalarAsync<bool>(connection, "SELECT indisunique FROM pg_index WHERE indexrelid = 'audit.ix_audit_events_chain_index'::regclass")).ShouldBeFalse();
            (await ScalarAsync<long>(connection,
                    $"SELECT count(*) FROM pg_partition_tree('audit.audit_events') t JOIN pg_index i ON i.indrelid = t.relid WHERE t.isleaf"))
                .ShouldBe(7 * (MonthsWithRows.Count(m => m is >= -12 * 15 and < 0) + 5));
            (await IndexesAsync(connection, "virtualcare.rpm_readings")).ShouldContain("ix_rpm_readings_enrollment_id_type_measured_at");
            (await ScalarAsync<string>(connection,
                    "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'virtualcare.rpm_readings'::regclass AND contype = 'f'"))
                .ShouldBe("FOREIGN KEY (enrollment_id) REFERENCES virtualcare.rpm_enrollments(id) ON DELETE RESTRICT");

            // The audit trail stays append-only, on the table and on each partition.
            await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, "DELETE FROM audit.audit_events WHERE id = 1"));
            await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection,
                $"TRUNCATE audit.audit_events_{thisMonth:yyyy_MM}"));

            // New rows go where their time says, and the identity goes on from where it was.
            var newId = await ScalarAsync<long>(connection,
                "INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES (now(), 'security', 'it.after', 'Info') RETURNING id");
            newId.ShouldBeGreaterThan(lastAuditId);
            (await PartitionOfAsync(database.ConnectionString, "audit.audit_events", "id", newId)).ShouldBe($"audit.audit_events_{thisMonth:yyyy_MM}");
            foreach (var (monthsAhead, partition) in new[] { (2, $"_{thisMonth.AddMonths(2):yyyy_MM}"), (3, $"_{thisMonth.AddMonths(3):yyyy_MM}"), (4, "_default") })
            {
                var id = Guid.CreateVersion7();
                await ExecuteAsync(connection,
                    $"INSERT INTO platform.notification_logs (id, created_at, channel, template_key, status) VALUES ('{id}', '{Bound(thisMonth.AddMonths(monthsAhead))}', 'Sms', 'it', 'Queued')");
                (await PartitionOfAsync(database.ConnectionString, "platform.notification_logs", "id", id)).ShouldBe("platform.notification_logs" + partition);
            }
        }

        // EF Core finds rows by id as before, writes them, seals the audit trail on the partitioned table, and has
        // nothing in its model the migrations lack: the snapshot does not try to undo the partitioning.
        db.ChangeTracker.Clear();
        db.Database.HasPendingModelChanges().ShouldBeFalse();
        var reading = expected.First(r => r.Table == "virtualcare.rpm_readings");
        (await db.Set<RpmReading>().SingleAsync(r => r.Id == (Guid)reading.Id, Ct)).MeasuredAt.ShouldBe(new DateTimeOffset(reading.At));
        var (from, to) = (thisMonth.AddMonths(-2), thisMonth.AddMonths(-1));
        (await db.Set<AuditEvent>().CountAsync(e => e.OccurredAt >= new DateTimeOffset(from) && e.OccurredAt < new DateTimeOffset(to), Ct))
            .ShouldBe(expected.Count(r => r.Table == "audit.audit_events" && r.At >= from && r.At < to));
        var log = new NotificationLog { CreatedAt = DateTimeOffset.UtcNow, Channel = NotificationChannel.Sms, TemplateKey = "it.ef", Status = NotificationStatus.Queued };
        db.Add(log);
        await db.SaveChangesAsync(Ct);
        log.Status = NotificationStatus.Sent;
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
        (await db.Set<NotificationLog>().SingleAsync(l => l.Id == log.Id, Ct)).Status.ShouldBe(NotificationStatus.Sent);
        (await db.Set<NotificationLog>().Where(l => l.Id == log.Id).ExecuteDeleteAsync(Ct)).ShouldBe(1);
        (await db.Set<IntegrationLog>().CountAsync(l => l.CreatedAt < new DateTimeOffset(thisMonth), Ct))
            .ShouldBe(expected.Count(r => r.Table == "integrations.integration_logs" && r.At < thisMonth));
        (await new AuditChainService(db).SealPendingAsync(Ct)).ShouldBe(2);
        var verified = await new AuditChainService(db).VerifyAsync(Ct);
        (verified.Valid, verified.UnsealedCount).ShouldBe((true, 0));
        verified.CheckedCount.ShouldBe(before["audit.audit_events"].Rows + 1);

        // Down: plain tables again, with every row, the original primary key and the unique chain position.
        var partitioned = await ChecksumsAsync(database.ConnectionString);
        var lastBeforeDown = await ScalarAsync<long>(database.ConnectionString, "SELECT max(id) FROM audit.audit_events");
        await migrator.MigrateAsync(previous, Ct);

        (await ChecksumsAsync(database.ConnectionString)).ShouldBe(partitioned);
        (await RelationKindsAsync(database.ConnectionString)).ShouldAllBe(k => k == "r");
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            foreach (var table in Tables)
            {
                (await ScalarAsync<long>(connection, $"SELECT count(*) FROM pg_inherits WHERE inhparent = '{table}'::regclass")).ShouldBe(0, table);
                (await ScalarAsync<string>(connection, $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = '{table}'::regclass AND contype = 'p'"))
                    .ShouldBe("PRIMARY KEY (id)");
            }

            (await ScalarAsync<bool>(connection, "SELECT indisunique FROM pg_index WHERE indexrelid = 'audit.ix_audit_events_chain_index'::regclass")).ShouldBeTrue();
            (await IndexesAsync(connection, "audit.audit_events")).Count.ShouldBe(7);
            (await ScalarAsync<string>(connection, "SELECT pg_get_serial_sequence('audit.audit_events', 'id')")).ShouldBe("audit.audit_events_id_seq");
            (await ScalarAsync<long>(connection,
                    "INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES (now(), 'security', 'it.down', 'Info') RETURNING id"))
                .ShouldBeGreaterThan(lastBeforeDown);
            await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, "DELETE FROM audit.audit_events WHERE id = 1"));
            await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, "TRUNCATE audit.audit_events"));
        }

        (await new AuditChainService(db).VerifyAsync(Ct)).Valid.ShouldBeTrue();
    }

    /// <summary>
    /// Rows in each table for each of <see cref="MonthsWithRows"/>, one on the first instant of the month and two later in
    /// it, and the partition each should end up in.
    /// </summary>
    private static async Task<List<SeededRow>> SeedAsync(string connectionString, DateTime thisMonth)
    {
        var rows = new List<SeededRow>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        // An enrolment for the readings to belong to (their foreign key), with the patient and programme it needs.
        var (patient, program, enrollment, facility) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await ExecuteAsync(connection, $"""
            INSERT INTO patients.patients (id, uhid, first_name, full_name, gender, is_date_of_birth_estimated, blood_group, marital_status,
                category, registered_facility_id, registered_at, is_vip, is_deceased, is_active, created_at, is_deleted)
            VALUES ('{patient}', 'IT-PART-1', 'Partition', 'Partition Test', 'Female', false, 'Unknown', 'Unknown', 'General', '{facility}', now(),
                false, false, true, now(), false);
            INSERT INTO virtualcare.rpm_programs (id, code, name, kind, expected_readings_per_day, is_active, created_at, is_deleted)
            VALUES ('{program}', 'IT', 'Partition test', 'Hypertension', 2, true, now(), false);
            INSERT INTO virtualcare.rpm_enrollments (id, facility_id, patient_id, program_id, care_team_practitioner_ids, status, start_date, created_at, is_deleted)
            VALUES ('{enrollment}', '{facility}', '{patient}', '{program}', ARRAY[]::uuid[], 'Active', current_date, now(), false);
            """);

        foreach (var monthOffset in MonthsWithRows)
        {
            var month = thisMonth.AddMonths(monthOffset);
            var ownPartition = monthOffset is >= -12 * 15 and <= 3;
            foreach (var at in new[] { month, month.AddDays(14).AddHours(9.5), month.AddMonths(1).AddTicks(-10) })
            {
                var suffix = ownPartition ? $"_{month:yyyy_MM}" : "_default";
                var auditId = await ScalarAsync<long>(connection,
                    $"INSERT INTO audit.audit_events (occurred_at, category, action, severity, summary, details) VALUES ('{Bound(at)}', 'security', 'it.seed', 'Info', 'seeded', '{{\"n\": 1}}') RETURNING id");
                rows.Add(new("audit.audit_events", "id", auditId, at, "audit.audit_events" + suffix));

                var reading = Guid.CreateVersion7();
                await ExecuteAsync(connection, $"""
                    INSERT INTO virtualcare.rpm_readings (id, facility_id, enrollment_id, patient_id, type, value, unit, measured_at, received_at, is_out_of_range, severity, source)
                    VALUES ('{reading}', '{facility}', '{enrollment}', '{patient}', 'SystolicBp', 128.5, 'mmHg', '{Bound(at)}', '{Bound(at)}', false, 'Normal', 'Device')
                    """);
                rows.Add(new("virtualcare.rpm_readings", "id", reading, at, "virtualcare.rpm_readings" + suffix));

                var message = Guid.CreateVersion7();
                await ExecuteAsync(connection, $"""
                    INSERT INTO platform.notification_logs (id, created_at, channel, template_key, recipient, status, sent_at)
                    VALUES ('{message}', '{Bound(at)}', 'Sms', 'it.seed', '98XXXXXX10', 'Sent', '{Bound(at)}')
                    """);
                rows.Add(new("platform.notification_logs", "id", message, at, "platform.notification_logs" + suffix));

                var call = Guid.CreateVersion7();
                await ExecuteAsync(connection, $"""
                    INSERT INTO integrations.integration_logs (id, created_at, integration, provider, operation, direction, success, duration_ms, request)
                    VALUES ('{call}', '{Bound(at)}', 'sms', 'test', 'send', 'Outbound', true, 12, 'to=98XXXXXX10')
                    """);
                rows.Add(new("integrations.integration_logs", "id", call, at, "integrations.integration_logs" + suffix));
            }
        }

        return rows;
    }

    /// <summary>Each table's row count and the md5 of every row's text, in id order.</summary>
    private static async Task<Dictionary<string, (long Rows, string Md5)>> ChecksumsAsync(string connectionString)
    {
        var sums = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        foreach (var table in Tables)
        {
            await using var command = new NpgsqlCommand($"SELECT count(*), coalesce(md5(string_agg(t::text, '|' ORDER BY t.id)), '') FROM {table} t", connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            sums[table] = (reader.GetInt64(0), reader.GetString(1));
        }

        return sums;
    }

    /// <summary>pg_class.relkind of each table: <c>r</c> plain, <c>p</c> partitioned.</summary>
    private static async Task<List<string>> RelationKindsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        var kinds = new List<string>();
        foreach (var table in Tables)
        {
            kinds.Add((await ScalarAsync<char>(connection, $"SELECT relkind FROM pg_class WHERE oid = '{table}'::regclass")).ToString());
        }

        return kinds;
    }

    private static async Task<string> PartitionOfAsync(string connectionString, string table, string key, object id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT tableoid::regclass::text FROM {table} WHERE {key} = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<string>> IndexesAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand($"SELECT c.relname FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = '{table}'::regclass ORDER BY 1", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var names = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string KeyColumn(string table) => table switch
    {
        "audit.audit_events" => "occurred_at",
        "virtualcare.rpm_readings" => "measured_at",
        _ => "created_at",
    };

    /// <summary>A UTC time as a timestamptz literal, to the microsecond.</summary>
    private static string Bound(DateTime at) => at.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "+00";

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

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        return await ScalarAsync<T>(connection, sql);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <param name="Partition">The partition it should be in, schema-qualified, as <c>tableoid::regclass</c> shows it.</param>
    private sealed record SeededRow(string Table, string Key, object Id, DateTime At, string Partition);
}
