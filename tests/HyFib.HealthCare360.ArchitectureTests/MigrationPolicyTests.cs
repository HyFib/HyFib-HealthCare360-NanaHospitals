namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// Expand/contract (docs/deployment/DEPLOYMENT.md, section 7, "Going back"): a new migration adds to the database and takes
/// away nothing the release before it reads or writes, so that release still runs on the upgraded database and going back
/// needs no restore. What a migration would take away (<see cref="MigrationRule"/>) waits until the code has stopped using
/// it for a whole release; a contract step then takes it away, listed in <see cref="MigrationPolicy.ContractSteps"/> with
/// the expand step it completes and why. The migrations up to <see cref="MigrationPolicy.LastGrandfathered"/> came before
/// the rule. The rule reads the migrations' source files, so it runs without loading the server.
/// </summary>
public sealed class MigrationPolicyTests
{
    [Fact]
    public void No_new_migration_takes_away_what_the_previous_release_uses_unless_it_is_a_reviewed_contract_step()
    {
        var contracts = MigrationPolicy.ContractSteps.Select(step => step.Migration).ToHashSet(StringComparer.Ordinal);
        var found = Migrations()
            .Where(migration => !IsGrandfathered(migration.Id) && !contracts.Contains(migration.Id))
            .SelectMany(migration => MigrationRule.Findings(migration.Source).Select(finding => $"{migration.Id} {finding}"))
            .ToList();

        found.ShouldBeEmpty(
            "Add the new and keep what the release before uses (an expand step); take the old away a release after the code stopped " +
            "using it, in a contract step listed in MigrationPolicy.ContractSteps (docs/deployment/DEPLOYMENT.md, section 7)");
    }

    [Fact]
    public void Each_contract_step_takes_something_away_a_release_after_its_expand_step_and_says_why()
    {
        var migrations = Migrations().ToDictionary(migration => migration.Id, migration => migration.Source, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var step in MigrationPolicy.ContractSteps)
        {
            if (!migrations.TryGetValue(step.Migration, out var source))
            {
                problems.Add($"{step.Migration} is no migration.");
                continue;
            }

            if (IsGrandfathered(step.Migration))
            {
                problems.Add($"{step.Migration} came before the rule and need not be listed.");
            }

            if (MigrationRule.Findings(source).Count == 0)
            {
                problems.Add($"{step.Migration} takes nothing away and need not be listed.");
            }

            if (string.IsNullOrWhiteSpace(step.Reason))
            {
                problems.Add($"{step.Migration} does not say why the release before it no longer needs what it takes away.");
            }

            if (!migrations.ContainsKey(step.ExpandStep) || string.CompareOrdinal(step.ExpandStep, step.Migration) >= 0)
            {
                problems.Add($"{step.Migration} names {step.ExpandStep} as its expand step, which is no migration before it.");
            }
            else if (ReleaseOf(step.ExpandStep) == MigrationPolicy.Releases.Count)
            {
                problems.Add($"{step.Migration} follows {step.ExpandStep}, which no published release carries yet: it waits a release.");
            }
            else if (ReleaseOf(step.Migration) <= ReleaseOf(step.ExpandStep))
            {
                problems.Add($"{step.Migration} comes in the same release as its expand step {step.ExpandStep}: it waits a release.");
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_starts_after_a_real_migration_and_the_releases_name_real_migrations_in_order()
    {
        var ids = Migrations().Select(migration => migration.Id).ToList();
        ids.ShouldContain(id => id.StartsWith(MigrationPolicy.LastGrandfathered + "_", StringComparison.Ordinal));
        foreach (var release in MigrationPolicy.Releases)
        {
            ids.ShouldContain(release.LastMigration, $"release {release.Version}");
        }

        MigrationPolicy.Releases.Select(release => release.LastMigration).ShouldBeInOrder(SortDirection.Ascending, StringComparer.Ordinal);
        MigrationPolicy.Releases.Select(release => release.Version).ShouldBeUnique();
    }

    [Fact]
    public void A_column_dropped_by_Up_is_found_and_one_dropped_by_Down_is_not()
    {
        const string Drop = """
            migrationBuilder.DropColumn(
                name: "reminder24h_sent_at",
                schema: "scheduling",
                table: "appointments");
            """;
        const string Add = """
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reminder24h_sent_at",
                schema: "scheduling",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);
            """;

        MigrationRule.Findings(Migration(up: Drop, down: Add)).ShouldBe(["drops the column scheduling.appointments.reminder24h_sent_at"]);
        MigrationRule.Findings(Migration(up: Add, down: Drop)).ShouldBeEmpty();
    }

    [Fact]
    public void Dropping_or_renaming_a_table_a_column_a_schema_or_a_sequence_is_found()
    {
        const string Up = """
            migrationBuilder.DropTable(
                name: "legacy_tokens",
                schema: "patients");

            migrationBuilder.RenameTable(
                name: "rate_cards",
                schema: "billing",
                newName: "rate_lists");

            migrationBuilder.RenameColumn(
                name: "procedure_codes",
                schema: "billing",
                table: "pre_authorizations",
                newName: "legacy_procedure_text");

            migrationBuilder.DropSchema(name: "staging");

            migrationBuilder.DropSequence(name: "bill_numbers", schema: "billing");
            """;

        MigrationRule.Findings(Migration(Up)).ShouldBe(
        [
            "drops the table patients.legacy_tokens",
            "renames the table billing.rate_cards to billing.rate_lists",
            "renames the column billing.pre_authorizations.procedure_codes to legacy_procedure_text",
            "drops the schema staging",
            "drops the sequence billing.bill_numbers",
        ]);
    }

    [Theory]
    [InlineData("""nullable: false, oldClrType: typeof(string), oldType: "character varying(64)", oldMaxLength: 64, oldNullable: true""", "it no longer takes NULL")]
    [InlineData("""type: "character varying(32)", maxLength: 32, nullable: true, oldType: "character varying(64)", oldMaxLength: 64, oldNullable: true""", "its type goes from character varying(64) to character varying(32)")]
    [InlineData("""type: "character varying(64)", maxLength: 64, nullable: true, oldType: "text", oldNullable: true""", "its type goes from text to character varying(64)")]
    [InlineData("""type: "integer", nullable: false, oldClrType: typeof(long), oldType: "bigint" """, "its type goes from bigint to integer")]
    [InlineData("""type: "numeric(14,4)", nullable: false, oldType: "numeric(14,2)" """, "its type goes from numeric(14,2) to numeric(14,4)")]
    [InlineData("""type: "uuid", nullable: true, oldType: "character varying(36)", oldMaxLength: 36, oldNullable: true""", "its type goes from character varying(36) to uuid")]
    [InlineData("""maxLength: 32, nullable: true, oldMaxLength: 64, oldNullable: true""", "its length goes from 64 to 32")]
    [InlineData("""type: "integer", nullable: false, oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 0""", "it loses the default the previous release inserts by")]
    public void A_column_narrowed_is_found(string arguments, string how) =>
        MigrationRule.Findings(Migration(AlterColumn(arguments))).ShouldBe([$"narrows the column patients.queue_tokens.label: {how}"]);

    [Theory]
    [InlineData("""type: "character varying(64)", maxLength: 64, nullable: true, oldClrType: typeof(string), oldType: "character varying(64)", oldMaxLength: 64""")]
    [InlineData("""type: "character varying(2700)", maxLength: 2700, nullable: false, oldClrType: typeof(string), oldType: "character varying(700)", oldMaxLength: 700""")]
    [InlineData("""type: "text", nullable: true, oldType: "character varying(64)", oldMaxLength: 64, oldNullable: true""")]
    [InlineData("""type: "character varying", nullable: true, oldType: "character varying(64)", oldMaxLength: 64, oldNullable: true""")]
    [InlineData("""type: "bigint", nullable: false, oldClrType: typeof(int), oldType: "integer" """)]
    [InlineData("""type: "numeric(18,2)", nullable: false, oldType: "numeric(14,2)" """)]
    [InlineData("""type: "integer", nullable: false, defaultValue: 1, oldType: "integer", oldDefaultValue: 0""")]
    public void A_column_widened_or_given_another_default_is_not_found(string arguments) =>
        MigrationRule.Findings(Migration(AlterColumn(arguments))).ShouldBeEmpty();

    [Theory]
    [InlineData("""type: "character varying(64)", maxLength: 64, nullable: false""", true)]
    [InlineData("""type: "character varying(64)", maxLength: 64""", true)]
    [InlineData("""type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "Change" """, false)]
    [InlineData("""type: "boolean", nullable: false, defaultValue: false""", false)]
    [InlineData("""type: "timestamp with time zone", nullable: false, defaultValueSql: "now()" """, false)]
    [InlineData("""type: "character varying(64)", maxLength: 64, nullable: true""", false)]
    public void A_required_column_added_with_no_default_is_found(string arguments, bool found)
    {
        var findings = MigrationRule.Findings(Migration($"""
            migrationBuilder.AddColumn<string>(
                name: "purpose",
                schema: "patients",
                table: "patient_phone_changes",
                {arguments});
            """));

        findings.ShouldBe(found ? ["adds the column patients.patient_phone_changes.purpose as required with no default, which the previous release's inserts leave out"] : []);
    }

    [Theory]
    [InlineData("ALTER TABLE billing.bills DROP COLUMN legacy_number;", "drops a column: ALTER TABLE billing.bills DROP COLUMN legacy_number")]
    [InlineData("DROP TABLE IF EXISTS billing.rate_cards;", "drops a table: DROP TABLE IF EXISTS billing.rate_cards")]
    [InlineData("ALTER TABLE billing.rate_cards RENAME TO rate_lists;", "renames a table or a column: ALTER TABLE billing.rate_cards RENAME TO rate_lists")]
    [InlineData("alter table billing.bills rename column notes to remarks;", "renames a table or a column: alter table billing.bills rename column notes to remarks")]
    [InlineData("ALTER TABLE billing.bills ALTER COLUMN notes TYPE character varying(100);", "changes a column's type: ALTER TABLE billing.bills ALTER COLUMN notes TYPE character varying(100)")]
    [InlineData("ALTER TABLE billing.bills ALTER COLUMN notes SET NOT NULL;", "makes a column NOT NULL: ALTER TABLE billing.bills ALTER COLUMN notes SET NOT NULL")]
    [InlineData("ALTER TABLE billing.bills ALTER COLUMN status DROP DEFAULT;", "drops a column's default: ALTER TABLE billing.bills ALTER COLUMN status DROP DEFAULT")]
    public void Sql_that_takes_something_away_is_found(string sql, string finding)
    {
        MigrationRule.Findings(Migration($"migrationBuilder.Sql(\"{sql}\");")).ShouldBe([$"runs SQL that {finding}"]);
        MigrationRule.Findings(Migration(up: "migrationBuilder.Sql(\"SELECT 1;\");", down: $"migrationBuilder.Sql(\"{sql}\");")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("UPDATE patients.patient_phone_changes SET status = 'Superseded' WHERE purpose = 'Verify';")]
    [InlineData("CREATE INDEX ix_bills_notes ON billing.bills (notes);")]
    [InlineData("ALTER INDEX billing.ix_bills_notes RENAME TO ix_bills_remarks;")]
    [InlineData("ALTER TABLE billing.bills RENAME CONSTRAINT ck_bills_total TO ck_bills_net;")]
    [InlineData("ALTER TABLE billing.bills ADD COLUMN remarks text;")]
    [InlineData("DROP INDEX IF EXISTS billing.ix_bills_notes;")]
    public void Sql_that_takes_nothing_away_is_not_found(string sql) =>
        MigrationRule.Findings(Migration($"migrationBuilder.Sql(\"{sql}\");")).ShouldBeEmpty();

    [Fact]
    public void Sql_kept_in_a_constant_of_the_migration_is_read_as_its_upgrade()
    {
        const string MoveNotes = """
            public const string MoveNotes = @"
                UPDATE billing.bills SET remarks = notes;
                ALTER TABLE billing.bills DROP COLUMN notes;";
            """;

        MigrationRule.Findings(Migration(up: "migrationBuilder.Sql(MoveNotes);", members: MoveNotes))
            .ShouldBe(["runs SQL that drops a column: ALTER TABLE billing.bills DROP COLUMN notes"]);
    }

    [Fact]
    public void A_table_the_upgrade_makes_and_drops_again_is_not_found()
    {
        const string Up = """"
            migrationBuilder.Sql("""
                CREATE TEMP TABLE staging_numbers AS SELECT key, current_value FROM platform.number_series;
                UPDATE platform.number_series s SET current_value = n.current_value + 1 FROM staging_numbers n WHERE n.key = s.key;
                DROP TABLE staging_numbers;
                """);
            """";

        MigrationRule.Findings(Migration(Up)).ShouldBeEmpty();
    }

    [Fact]
    public void A_constant_or_a_helper_only_Down_uses_is_not_read_as_the_upgrade_and_one_Up_uses_is()
    {
        const string Members = """
            private const string PutNotesBack = "ALTER TABLE billing.bills DROP COLUMN remarks;";

            private static string Retire => "ALTER TABLE billing.bills RENAME COLUMN notes TO old_notes;";

            private static void DropLegacy(MigrationBuilder migrationBuilder) =>
                migrationBuilder.DropColumn(name: "legacy_number", schema: "billing", table: "bills");
            """;

        MigrationRule.Findings(Migration(up: "migrationBuilder.Sql(\"SELECT 1;\");", down: "migrationBuilder.Sql(PutNotesBack);", members: Members))
            .ShouldBeEmpty();
        MigrationRule.Findings(Migration(up: "migrationBuilder.Sql(Retire);\nDropLegacy(migrationBuilder);", members: Members)).ShouldBe(
        [
            "runs SQL that renames a table or a column: ALTER TABLE billing.bills RENAME COLUMN notes TO old_notes",
            "drops the column billing.bills.legacy_number",
        ]);
    }

    [Fact]
    public void Comments_and_text_count_for_nothing_and_braces_in_text_do_not_end_the_upgrade_early()
    {
        const string Up = """
            // migrationBuilder.DropTable(name: "patients", schema: "patients");
            /* migrationBuilder.DropColumn(name: "uhid", schema: "patients", table: "patients"); */
            var note = "migrationBuilder.DropTable(name: \"patients\");";
            var quote = '"';
            migrationBuilder.Sql(@"UPDATE platform.settings SET value = '{""shown"": true}' WHERE key = 'x';");
            migrationBuilder.Sql($"UPDATE platform.settings SET value = '{(quote == '"' ? "a" : "b")}' WHERE key = 'y';");
            migrationBuilder.Sql("SELECT '}';");
            migrationBuilder.DropColumn(name: "flag_note", schema: "patients", table: "patient_flags");
            """;

        MigrationRule.Findings(Migration(Up)).ShouldBe(["drops the column patients.patient_flags.flag_note"]);
    }

    [Fact]
    public void Sql_that_names_its_table_through_format_is_found()
    {
        const string Up = """"
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    EXECUTE format('DROP TABLE %s', old_table);
                    EXECUTE format('ALTER TABLE %s RENAME TO %I', staging, 'audit_events');
                END $$;
                """);
            """";

        MigrationRule.Findings(Migration(Up)).ShouldBe(
        [
            "runs SQL that drops a table: EXECUTE format('DROP TABLE %s', old_table)",
            "runs SQL that renames a table or a column: EXECUTE format('ALTER TABLE %s RENAME TO %I', staging, 'audit_events')",
        ]);
    }

    [Fact]
    public void The_rule_would_have_stopped_the_grandfathered_migrations_that_took_away_what_the_release_before_used()
    {
        var migrations = Migrations().ToDictionary(migration => migration.Id, migration => migration.Source, StringComparer.Ordinal);

        MigrationRule.Findings(migrations["20261109095942_AppointmentRemindersOwnTable"]).ShouldBe(
        [
            "drops the column scheduling.appointments.reminder24h_sent_at",
            "drops the column scheduling.appointments.reminder2h_sent_at",
        ]);
        MigrationRule.Findings(migrations["20261109155218_GapBillingCodedProceduresVisitPayer"]).ShouldBe(
            ["renames the column billing.pre_authorizations.procedure_codes to legacy_procedure_text"]);

        // An expand step: columns and tables added, NULL allowed where it was not, a required column added with its default.
        MigrationRule.Findings(migrations["20261113130000_CounterTerminalPayments"]).ShouldBeEmpty();
        MigrationRule.Findings(migrations["20261114110000_PatientPhoneVerification"]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("character varying(64)", "character varying(128)", true)]
    [InlineData("character varying(64)", "text", true)]
    [InlineData("text", "character varying(64)", false)]
    [InlineData("character varying", "character varying(64)", false)]
    [InlineData("smallint", "bigint", true)]
    [InlineData("bigint", "integer", false)]
    [InlineData("numeric(14,2)", "numeric(18, 2)", true)]
    [InlineData("numeric(14,2)", "numeric(14,3)", false)]
    [InlineData("numeric(14,2)", "numeric", true)]
    [InlineData("real", "double precision", true)]
    [InlineData("timestamp without time zone", "timestamp with time zone", false)]
    public void A_type_widens_only_when_it_holds_everything_it_held(string from, string to, bool widens) =>
        MigrationRule.Widens(from, to).ShouldBe(widens);

    private static bool IsGrandfathered(string id) => string.CompareOrdinal(id[..14], MigrationPolicy.LastGrandfathered) <= 0;

    /// <summary>The index of the first published release that carries <paramref name="migration"/>; the number of releases when none does yet.</summary>
    private static int ReleaseOf(string migration)
    {
        var releases = MigrationPolicy.Releases;
        for (var i = 0; i < releases.Count; i++)
        {
            if (string.CompareOrdinal(migration, releases[i].LastMigration) <= 0)
            {
                return i;
            }
        }

        return releases.Count;
    }

    /// <summary>The server's migrations, each by its file name without ".cs", with its source.</summary>
    private static List<(string Id, string Source)> Migrations()
    {
        var folder = Path.Combine(RepositoryRoot(), "src", "server", "HyFib.HealthCare360.Server", "Persistence", "Migrations");
        return [.. Directory.EnumerateFiles(folder, "*.cs")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => name.Length > 15 && name[..14].All(char.IsAsciiDigit) && name[14] == '_' && !name.EndsWith(".Designer", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => (name, File.ReadAllText(Path.Combine(folder, name + ".cs"))))];
    }

    /// <summary>
    /// A migration whose Up and Down run <paramref name="up"/> and <paramref name="down"/>, as EF Core writes one, with
    /// <paramref name="members"/> beside them.
    /// </summary>
    private static string Migration(string up, string down = "", string members = "") => $$"""
        using Microsoft.EntityFrameworkCore.Migrations;

        #nullable disable

        namespace HyFib.HealthCare360.Server.Persistence.Migrations
        {
            /// <inheritdoc />
            public partial class Sample : Migration
            {
        {{members}}

                /// <inheritdoc />
                protected override void Up(MigrationBuilder migrationBuilder)
                {
        {{up}}
                }

                /// <inheritdoc />
                protected override void Down(MigrationBuilder migrationBuilder)
                {
        {{down}}
                }
            }
        }
        """;

    private static string AlterColumn(string arguments) => $"""
        migrationBuilder.AlterColumn<string>(
            name: "label",
            schema: "patients",
            table: "queue_tokens",
            {arguments});
        """;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyFib.HealthCare360.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (HyFib.HealthCare360.slnx) was not found above the test's folder.");
    }
}
