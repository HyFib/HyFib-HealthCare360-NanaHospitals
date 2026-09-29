using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>Planning the adoption of a database created before migrations existed (pre-release EnsureCreated databases).</summary>
public sealed class MigrationBaselineTests
{
    [Fact]
    public void A_database_that_matches_the_first_migration_is_only_recorded_as_migrated()
    {
        var plan = MigrationBaseline.Plan(Migration(), Catalog());

        plan.Blockers.ShouldBeEmpty();
        plan.Changes.ShouldBeEmpty();
        plan.Operations.ShouldAllBe(o => o is EnsureSchemaOperation || o is AlterDatabaseOperation);
    }

    [Fact]
    public void A_table_added_since_the_database_was_created_is_created_with_its_indexes()
    {
        var plan = MigrationBaseline.Plan(Migration(), Catalog(withoutTable: "dashboards"));

        plan.Blockers.ShouldBeEmpty();
        plan.Operations.OfType<CreateTableOperation>().Single().Name.ShouldBe("dashboards");
        plan.Operations.OfType<CreateIndexOperation>().Single().Name.ShouldBe("ix_dashboards_owner_id");
        plan.Changes.ShouldBe(["create table analytics.dashboards"]);
    }

    [Fact]
    public void A_nullable_column_added_since_is_added()
    {
        var plan = MigrationBaseline.Plan(Migration(), Catalog(withoutColumn: "delivered_at"));

        plan.Blockers.ShouldBeEmpty();
        var column = plan.Operations.OfType<AddColumnOperation>().Single();
        (column.Schema, column.Table, column.Name).ShouldBe(("platform", "notification_logs", "delivered_at"));
        plan.Changes.ShouldBe(["add column platform.notification_logs.delivered_at"]);
    }

    [Fact]
    public void A_missing_required_column_without_a_default_stops_the_adoption()
    {
        var plan = MigrationBaseline.Plan(Migration(), Catalog(withoutColumn: "status"));

        plan.Blockers.ShouldHaveSingleItem().ShouldContain("platform.notification_logs.status is missing");
    }

    [Fact]
    public void A_column_of_another_type_stops_the_adoption()
    {
        var plan = MigrationBaseline.Plan(Migration(), Catalog(retype: ("status", "integer", Nullable: false)));

        plan.Blockers.ShouldHaveSingleItem().ShouldContain("status is integer; this version needs character varying(32)");
    }

    [Fact]
    public void A_shorter_text_column_is_lengthened()
    {
        var plan = MigrationBaseline.Plan(Migration(), Catalog(retype: ("status", "character varying(16)", Nullable: false)));

        plan.Blockers.ShouldBeEmpty();
        plan.Operations.OfType<SqlOperation>().Single().Sql
            .ShouldBe("ALTER TABLE \"platform\".\"notification_logs\" ALTER COLUMN \"status\" TYPE character varying(32);");
    }

    [Fact]
    public void Nullability_is_brought_in_line()
    {
        var relaxed = MigrationBaseline.Plan(Migration(), Catalog(retype: ("delivered_at", "timestamp with time zone", Nullable: false)));
        relaxed.Operations.OfType<SqlOperation>().Single().Sql.ShouldEndWith("ALTER COLUMN \"delivered_at\" DROP NOT NULL;");

        var required = MigrationBaseline.Plan(Migration(), Catalog(retype: ("status", "character varying(32)", Nullable: true)));
        required.Operations.OfType<SqlOperation>().Single().Sql.ShouldEndWith("ALTER COLUMN \"status\" SET NOT NULL;");
    }

    [Fact]
    public void The_row_version_system_column_is_never_created_or_compared()
    {
        // xmin is PostgreSQL's own column: the catalog query does not list it, and it must not look missing.
        var plan = MigrationBaseline.Plan(Migration(), Catalog());

        plan.Operations.OfType<AddColumnOperation>().ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_index_and_constraint_on_an_existing_table_are_added_after_its_columns()
    {
        var plan = MigrationBaseline.Plan(
            Migration(),
            Catalog(withoutIndex: "ix_notification_logs_provider_message_id", withoutConstraint: "fk_notification_logs_users_user_id", withoutColumn: "delivered_at"));

        plan.Blockers.ShouldBeEmpty();
        var kinds = plan.Operations.Select(o => o.GetType().Name).ToList();
        kinds.IndexOf(nameof(AddColumnOperation)).ShouldBeLessThan(kinds.IndexOf(nameof(AddForeignKeyOperation)));
        kinds.IndexOf(nameof(AddForeignKeyOperation)).ShouldBeLessThan(kinds.IndexOf(nameof(CreateIndexOperation)));
        var foreignKey = plan.Operations.OfType<AddForeignKeyOperation>().Single();
        (foreignKey.Schema, foreignKey.Table).ShouldBe(("platform", "notification_logs"));
    }

    [Fact]
    public void An_operation_that_cannot_be_checked_stops_the_adoption()
    {
        var plan = MigrationBaseline.Plan([.. Migration(), new SqlOperation { Sql = "UPDATE x SET y = 1" }], Catalog());

        plan.Blockers.ShouldHaveSingleItem().ShouldContain("SqlOperation");
    }

    [Theory]
    [InlineData("character varying(16)", "character varying(32)", true)]
    [InlineData("character varying(16)", "text", true)]
    [InlineData("character varying(32)", "character varying(16)", false)]
    [InlineData("integer", "bigint", false)]
    public void Only_a_longer_text_column_counts_as_lengthening(string actual, string expected, bool widening) =>
        MigrationBaseline.IsWidening(actual, expected).ShouldBe(widening);

    [Fact]
    public void Types_compare_as_PostgreSQL_spells_them() =>
        MigrationBaseline.SameType("Timestamp  with time zone", "timestamp with time zone").ShouldBeTrue();

    /// <summary>A two-table stand-in for InitialCreate, shaped like EF's generated operations.</summary>
    private static List<MigrationOperation> Migration()
    {
        var logs = new CreateTableOperation
        {
            Schema = "platform",
            Name = "notification_logs",
            PrimaryKey = new AddPrimaryKeyOperation { Schema = "platform", Table = "notification_logs", Name = "pk_notification_logs", Columns = ["id"] },
        };
        logs.Columns.Add(Column("platform", "notification_logs", "id", "uuid", nullable: false));
        logs.Columns.Add(Column("platform", "notification_logs", "status", "character varying(32)", nullable: false));
        logs.Columns.Add(Column("platform", "notification_logs", "delivered_at", "timestamp with time zone", nullable: true));
        logs.Columns.Add(Column("platform", "notification_logs", "user_id", "uuid", nullable: true));
        logs.Columns.Add(Column("platform", "notification_logs", "xmin", "xid", nullable: false));
        logs.ForeignKeys.Add(new AddForeignKeyOperation
        {
            Schema = "platform",
            Table = "notification_logs",
            Name = "fk_notification_logs_users_user_id",
            Columns = ["user_id"],
            PrincipalSchema = "platform",
            PrincipalTable = "users",
            PrincipalColumns = ["id"],
        });

        var dashboards = new CreateTableOperation
        {
            Schema = "analytics",
            Name = "dashboards",
            PrimaryKey = new AddPrimaryKeyOperation { Schema = "analytics", Table = "dashboards", Name = "pk_dashboards", Columns = ["id"] },
        };
        dashboards.Columns.Add(Column("analytics", "dashboards", "id", "uuid", nullable: false));
        dashboards.Columns.Add(Column("analytics", "dashboards", "owner_id", "uuid", nullable: false));

        return
        [
            new EnsureSchemaOperation { Name = "platform" },
            new EnsureSchemaOperation { Name = "analytics" },
            new AlterDatabaseOperation(),
            logs,
            dashboards,
            new CreateIndexOperation { Schema = "platform", Table = "notification_logs", Name = "ix_notification_logs_provider_message_id", Columns = ["status"] },
            new CreateIndexOperation { Schema = "analytics", Table = "dashboards", Name = "ix_dashboards_owner_id", Columns = ["owner_id"] },
        ];
    }

    private static AddColumnOperation Column(string schema, string table, string name, string type, bool nullable) =>
        new() { Schema = schema, Table = table, Name = name, ColumnType = type, IsNullable = nullable, ClrType = typeof(string) };

    /// <summary>
    /// The catalog of a database created from <see cref="Migration"/>, minus or changed as asked; <c>retype</c> gives a
    /// column's type and whether the database allows it to be empty.
    /// </summary>
    private static DatabaseCatalog Catalog(
        string? withoutTable = null,
        string? withoutColumn = null,
        string? withoutIndex = null,
        string? withoutConstraint = null,
        (string Column, string Type, bool Nullable)? retype = null)
    {
        var tables = new HashSet<(string, string)>();
        var columns = new Dictionary<(string, string, string), CatalogColumn>();
        var indexes = new HashSet<(string, string)>();
        var constraints = new HashSet<(string, string, string)>();
        foreach (var operation in Migration())
        {
            switch (operation)
            {
                case CreateTableOperation table when table.Name != withoutTable:
                    tables.Add((table.Schema!, table.Name));
                    constraints.Add((table.Schema!, table.Name, table.PrimaryKey!.Name));
                    foreach (var foreignKey in table.ForeignKeys.Where(f => f.Name != withoutConstraint))
                    {
                        constraints.Add((table.Schema!, table.Name, foreignKey.Name));
                    }

                    foreach (var column in table.Columns.Where(c => c.Name != withoutColumn && c.Name != "xmin"))
                    {
                        var (type, nullable) = retype is { } r && r.Column == column.Name ? (r.Type, r.Nullable) : (column.ColumnType!, column.IsNullable);
                        columns[(table.Schema!, table.Name, column.Name)] = new CatalogColumn(type, NotNull: !nullable);
                    }

                    break;

                case CreateIndexOperation index when index.Name != withoutIndex && index.Table != withoutTable:
                    indexes.Add((index.Schema!, index.Name));
                    break;
            }
        }

        return new DatabaseCatalog(tables, columns, indexes, constraints, new HashSet<(string, string)>());
    }
}
