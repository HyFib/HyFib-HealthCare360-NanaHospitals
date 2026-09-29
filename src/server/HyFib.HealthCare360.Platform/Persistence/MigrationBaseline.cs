using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// Start-up cannot bring the database up to date safely. The message says why and what to do; the database was left as it
/// was. Start-up does not retry it, because it would fail the same way again.
/// </summary>
public sealed class DatabaseUpgradeException : Exception
{
    public DatabaseUpgradeException()
    {
    }

    public DatabaseUpgradeException(string message)
        : base(message)
    {
    }

    public DatabaseUpgradeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The application's tables, columns, indexes and constraints as PostgreSQL reports them.</summary>
internal sealed record DatabaseCatalog(
    IReadOnlySet<(string Schema, string Table)> Tables,
    IReadOnlyDictionary<(string Schema, string Table, string Column), CatalogColumn> Columns,
    IReadOnlySet<(string Schema, string Index)> Indexes,
    IReadOnlySet<(string Schema, string Table, string Constraint)> Constraints,
    IReadOnlySet<(string Schema, string Sequence)> Sequences);

/// <param name="Type">PostgreSQL's own spelling of the type (<c>format_type</c>), e.g. <c>character varying(64)</c>.</param>
internal sealed record CatalogColumn(string Type, bool NotNull);

/// <summary>What adopting a database takes: the operations to run, a description of each change, and anything that prevents it.</summary>
internal sealed record BaselinePlan(IReadOnlyList<MigrationOperation> Operations, IReadOnlyList<string> Changes, IReadOnlyList<string> Blockers);

/// <summary>
/// Adopts a database created before HC360 had migrations (pre-release builds created the schema with EnsureCreated, which
/// records no migration history). Applying the first migration to it would create every table again and fail on the first
/// one that exists. Instead the database is compared with what the first migration creates: anything missing that can be
/// added safely (tables, nullable columns, indexes, constraints, longer text columns, nullability) is added, the first
/// migration is recorded as applied, all in one transaction, and later migrations then apply as usual. A difference that
/// cannot be fixed without risk to the data (another column type, a required column without a default) stops start-up
/// with the list of differences, before anything is changed or backed up.
/// </summary>
internal static partial class MigrationBaseline
{
    /// <summary>The migration history table (<c>MigrationsHistoryTable</c> in the platform registration).</summary>
    public const string HistorySchema = "platform";
    public const string HistoryTable = "__ef_migrations_history";

    // PostgreSQL's system columns (Npgsql maps row versions to xmin): never created or compared.
    private static readonly HashSet<string> SystemColumns = ["xmin", "xmax", "cmin", "cmax", "ctid", "tableoid"];

    /// <summary>
    /// True when the migration history table exists. A new database and a pre-migration one have none; asking EF for their
    /// applied migrations would log a failed query, which looks alarming in the log of an upgrade that goes well.
    /// </summary>
    public static Task<bool> HistoryExistsAsync(HospitalDbContext db, CancellationToken ct) =>
        ExistsAsync(
            db,
            "SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relname = @history)",
            [new NpgsqlParameter("schema", HistorySchema), new NpgsqlParameter("history", HistoryTable)],
            ct);

    /// <summary>True when the database has application tables (anything besides the migration history).</summary>
    public static Task<bool> HasApplicationTablesAsync(HospitalDbContext db, CancellationToken ct) =>
        ExistsAsync(
            db,
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p') AND n.nspname = ANY(@schemas) AND c.relname <> @history)
            """,
            [
                new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = db.Model.GetEntityTypes().Select(e => e.GetSchema() ?? "public").Distinct().ToArray(),
                },
                new NpgsqlParameter("history", HistoryTable),
            ],
            ct);

    private static async Task<bool> ExistsAsync(HospitalDbContext db, string sql, NpgsqlParameter[] parameters, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
            command.Parameters.AddRange(parameters);
            return (bool)(await command.ExecuteScalarAsync(ct))!;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Compares the database with the first migration and plans the changes, without changing anything.</summary>
    /// <exception cref="DatabaseUpgradeException">The database differs in a way that cannot be fixed safely.</exception>
    public static async Task<Adoption> PrepareAsync(HospitalDbContext db, CancellationToken ct)
    {
        var migrations = db.GetService<IMigrationsAssembly>();
        var (id, type) = migrations.Migrations.OrderBy(m => m.Key, StringComparer.Ordinal).First();
        var migration = migrations.CreateMigration(type, db.Database.ProviderName!);
        var schemas = migration.UpOperations.OfType<EnsureSchemaOperation>().Select(s => s.Name)
            .Concat(migration.UpOperations.OfType<CreateTableOperation>().Select(t => t.Schema ?? "public"))
            .Distinct()
            .ToArray();

        var plan = Plan(migration.UpOperations, await ReadCatalogAsync(db, schemas, ct));
        if (plan.Blockers.Count > 0)
        {
            throw new DatabaseUpgradeException(
                "This database was created by a pre-release version of HC360, before database migrations existed, and it differs from " +
                "what this version expects in ways that cannot be fixed automatically without risk to its data. Nothing was changed. " +
                $"Differences: {string.Join(" ", plan.Blockers.Take(20))}{(plan.Blockers.Count > 20 ? $" (and {plan.Blockers.Count - 20} more)" : string.Empty)} " +
                "Keep running the previous version and contact HyFib support to move the data across.");
        }

        return new Adoption(id, migration, plan);
    }

    /// <summary>
    /// The changes that make an existing database match what <paramref name="migration"/> creates. Pure: the database is
    /// described by <paramref name="catalog"/>.
    /// </summary>
    public static BaselinePlan Plan(IReadOnlyList<MigrationOperation> migration, DatabaseCatalog catalog)
    {
        var operations = new List<MigrationOperation>();
        var constraints = new List<MigrationOperation>();
        var indexes = new List<MigrationOperation>();
        var changes = new List<string>();
        var blockers = new List<string>();
        var createdTables = new HashSet<(string, string)>();

        foreach (var operation in migration)
        {
            switch (operation)
            {
                case EnsureSchemaOperation or AlterDatabaseOperation:
                    // PostgreSQL creates schemas and extensions only when they do not exist.
                    operations.Add(operation);
                    break;

                case CreateSequenceOperation sequence when !catalog.Sequences.Contains((Schema(sequence.Schema), sequence.Name)):
                    operations.Add(sequence);
                    changes.Add($"create sequence {Name(sequence.Schema, sequence.Name)}");
                    break;

                case CreateSequenceOperation:
                    break;

                case CreateTableOperation table when !catalog.Tables.Contains((Schema(table.Schema), table.Name)):
                    operations.Add(table);
                    createdTables.Add((Schema(table.Schema), table.Name));
                    changes.Add($"create table {Name(table.Schema, table.Name)}");
                    break;

                case CreateTableOperation table:
                    ReconcileTable(table, catalog, operations, constraints, changes, blockers);
                    break;

                case CreateIndexOperation index when !catalog.Indexes.Contains((Schema(index.Schema), index.Name)):
                    indexes.Add(index);
                    if (!createdTables.Contains((Schema(index.Schema), index.Table)))
                    {
                        changes.Add($"create index {index.Name} on {Name(index.Schema, index.Table)}");
                    }

                    break;

                case CreateIndexOperation:
                    break;

                default:
                    blockers.Add($"The first migration contains a {operation.GetType().Name}, which cannot be applied to an existing database automatically.");
                    break;
            }
        }

        return new BaselinePlan([.. operations, .. constraints, .. indexes], changes, blockers);
    }

    internal static bool SameType(string actual, string expected) =>
        string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);

    /// <summary>A longer text column keeps every value: <c>character varying(n)</c> to a larger n, or to <c>text</c>.</summary>
    internal static bool IsWidening(string actual, string expected)
    {
        var from = VarcharLength().Match(Normalize(actual));
        if (!from.Success)
        {
            return false;
        }

        var to = Normalize(expected);
        if (to == "text")
        {
            return true;
        }

        var toLength = VarcharLength().Match(to);
        return toLength.Success
            && int.Parse(toLength.Groups[1].Value, CultureInfo.InvariantCulture) > int.Parse(from.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static void ReconcileTable(
        CreateTableOperation table,
        DatabaseCatalog catalog,
        List<MigrationOperation> operations,
        List<MigrationOperation> constraints,
        List<string> changes,
        List<string> blockers)
    {
        var schema = Schema(table.Schema);
        var tableName = Name(table.Schema, table.Name);
        foreach (var column in table.Columns.Where(c => !SystemColumns.Contains(c.Name)))
        {
            if (!catalog.Columns.TryGetValue((schema, table.Name, column.Name), out var existing))
            {
                if (column.IsNullable || column.DefaultValue is not null || column.DefaultValueSql is not null || column.ComputedColumnSql is not null)
                {
                    column.Schema ??= table.Schema;
                    column.Table = table.Name;
                    operations.Add(column);
                    changes.Add($"add column {tableName}.{column.Name}");
                }
                else
                {
                    blockers.Add($"{tableName}.{column.Name} is missing, and a required column without a default cannot be added to existing rows.");
                }

                continue;
            }

            if (column.ColumnType is { } expected && !SameType(existing.Type, expected))
            {
                if (IsWidening(existing.Type, expected))
                {
                    operations.Add(new SqlOperation { Sql = $"ALTER TABLE {Quote(schema)}.{Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} TYPE {expected};" });
                    changes.Add($"lengthen {tableName}.{column.Name} from {existing.Type} to {expected}");
                }
                else
                {
                    blockers.Add($"{tableName}.{column.Name} is {existing.Type}; this version needs {expected}.");
                }
            }

            if (existing.NotNull && column.IsNullable)
            {
                operations.Add(new SqlOperation { Sql = $"ALTER TABLE {Quote(schema)}.{Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} DROP NOT NULL;" });
                changes.Add($"allow {tableName}.{column.Name} to be empty");
            }
            else if (!existing.NotNull && !column.IsNullable)
            {
                // Fails (and the whole adoption with it) if a row has no value: that is reported, not guessed.
                operations.Add(new SqlOperation { Sql = $"ALTER TABLE {Quote(schema)}.{Quote(table.Name)} ALTER COLUMN {Quote(column.Name)} SET NOT NULL;" });
                changes.Add($"require a value in {tableName}.{column.Name}");
            }
        }

        var tableConstraints = new List<MigrationOperation>();
        if (table.PrimaryKey is { } primaryKey)
        {
            tableConstraints.Add(primaryKey);
        }

        tableConstraints.AddRange(table.UniqueConstraints);
        tableConstraints.AddRange(table.CheckConstraints);
        tableConstraints.AddRange(table.ForeignKeys);
        foreach (var constraint in tableConstraints)
        {
            var name = constraint switch
            {
                AddPrimaryKeyOperation key => key.Name,
                AddUniqueConstraintOperation unique => unique.Name,
                AddCheckConstraintOperation check => check.Name,
                AddForeignKeyOperation foreignKey => foreignKey.Name,
                _ => null,
            };
            if (name is null || catalog.Constraints.Contains((schema, table.Name, name)))
            {
                continue;
            }

            SetTable(constraint, table);
            constraints.Add(constraint);
            changes.Add($"add constraint {name} on {tableName}");
        }
    }

    // Constraints built inside a CreateTable operation normally carry its table already; set them in case they do not.
    private static void SetTable(MigrationOperation constraint, CreateTableOperation table)
    {
        switch (constraint)
        {
            case AddPrimaryKeyOperation key:
                key.Schema ??= table.Schema;
                key.Table = string.IsNullOrEmpty(key.Table) ? table.Name : key.Table;
                break;
            case AddUniqueConstraintOperation unique:
                unique.Schema ??= table.Schema;
                unique.Table = string.IsNullOrEmpty(unique.Table) ? table.Name : unique.Table;
                break;
            case AddCheckConstraintOperation check:
                check.Schema ??= table.Schema;
                check.Table = string.IsNullOrEmpty(check.Table) ? table.Name : check.Table;
                break;
            case AddForeignKeyOperation foreignKey:
                foreignKey.Schema ??= table.Schema;
                foreignKey.Table = string.IsNullOrEmpty(foreignKey.Table) ? table.Name : foreignKey.Table;
                break;
        }
    }

    private static string Normalize(string type) => WhiteSpace().Replace(type.Trim().ToLowerInvariant(), " ");

    private static string Schema(string? schema) => schema ?? "public";

    private static string Name(string? schema, string name) => $"{Schema(schema)}.{name}";

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<DatabaseCatalog> ReadCatalogAsync(HospitalDbContext db, string[] schemas, CancellationToken ct)
    {
        var tables = new HashSet<(string, string)>();
        var columns = new Dictionary<(string, string, string), CatalogColumn>();
        var indexes = new HashSet<(string, string)>();
        var constraints = new HashSet<(string, string, string)>();
        var sequences = new HashSet<(string, string)>();

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await ReadAsync(connection, schemas,
                "SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND n.nspname = ANY(@schemas)",
                r => tables.Add((r.GetString(0), r.GetString(1))), ct);
            await ReadAsync(connection, schemas,
                """
                SELECT n.nspname, c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
                FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped AND n.nspname = ANY(@schemas)
                """,
                r => columns[(r.GetString(0), r.GetString(1), r.GetString(2))] = new CatalogColumn(r.GetString(3), r.GetBoolean(4)), ct);
            await ReadAsync(connection, schemas,
                "SELECT n.nspname, i.relname FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid JOIN pg_namespace n ON n.oid = i.relnamespace WHERE n.nspname = ANY(@schemas)",
                r => indexes.Add((r.GetString(0), r.GetString(1))), ct);
            await ReadAsync(connection, schemas,
                "SELECT n.nspname, t.relname, con.conname FROM pg_constraint con JOIN pg_class t ON t.oid = con.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace WHERE n.nspname = ANY(@schemas)",
                r => constraints.Add((r.GetString(0), r.GetString(1), r.GetString(2))), ct);
            await ReadAsync(connection, schemas,
                "SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'S' AND n.nspname = ANY(@schemas)",
                r => sequences.Add((r.GetString(0), r.GetString(1))), ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return new DatabaseCatalog(tables, columns, indexes, constraints, sequences);
    }

    private static async Task ReadAsync(NpgsqlConnection connection, string[] schemas, string sql, Action<NpgsqlDataReader> row, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas });
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            row(reader);
        }
    }

    [GeneratedRegex(@"^character varying\((\d+)\)$")]
    private static partial Regex VarcharLength();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    /// <summary>The planned changes for one database, applied with <see cref="ApplyAsync"/>.</summary>
    internal sealed class Adoption(string migrationId, Migration migration, BaselinePlan plan)
    {
        public BaselinePlan Plan => plan;

        /// <summary>Runs the planned changes and records the first migration as applied, in one transaction.</summary>
        public async Task ApplyAsync(HospitalDbContext db, ILogger logger, CancellationToken ct)
        {
            var model = migration.TargetModel is { } target
                ? db.GetService<IModelRuntimeInitializer>().Initialize(target, designTime: true, validationLogger: null)
                : null;
            var commands = db.GetService<IMigrationsSqlGenerator>().Generate(plan.Operations, model);
            var history = db.GetService<IHistoryRepository>();

            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                await using var transaction = await connection.BeginTransactionAsync(ct);
                foreach (var command in commands)
                {
                    await ExecuteAsync(connection, transaction, command.CommandText, ct);
                }

                await ExecuteAsync(connection, transaction, history.GetCreateIfNotExistsScript(), ct);
                await ExecuteAsync(connection, transaction, history.GetInsertScript(new HistoryRow(migrationId, ProductVersion(migration))), ct);
                await transaction.CommitAsync(ct);
            }
            catch (PostgresException ex)
            {
                throw new DatabaseUpgradeException(
                    "This database was created by a pre-release version of HC360, before database migrations existed, and bringing it up to " +
                    $"{migrationId} failed, so nothing was changed: {ex.MessageText}{(ex.Detail is { Length: > 0 } detail ? $" ({detail})" : string.Empty)}. " +
                    "Keep running the previous version and contact HyFib support.",
                    ex);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            logger.LogWarning(
                "Adopted a database created before migrations existed: recorded {Migration} as applied after {Count} change(s): {Changes}",
                migrationId,
                plan.Changes.Count,
                plan.Changes.Count == 0 ? "none" : string.Join("; ", plan.Changes));
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(ct);
        }

        private static string ProductVersion(Migration migration) =>
            migration.TargetModel?.GetProductVersion()
            ?? typeof(DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? "10.0.0";
    }
}
