using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <summary>
    /// SRS SCAL-04: the four tables that grow fastest are partitioned by calendar month (UTC) of the time each row is
    /// about: the audit trail (<c>occurred_at</c>), remote-monitoring readings (<c>measured_at</c>), the patient and staff
    /// message log (<c>created_at</c>) and the integration log (<c>created_at</c>). Each table is made again as a
    /// partitioned table with the same columns, defaults, checks and identity; a partition for this month, the next three
    /// and every month of the last 15 years that has rows, and a default partition for anything else; its rows copied
    /// (the counts must match); the old table dropped and the new one given its name, primary key, indexes, foreign keys
    /// and triggers, from the old table's own definitions; and the identity's sequence carried over where it continues.
    /// <para>
    /// PostgreSQL wants the partition key in every primary key and unique index of a partitioned table, so the primary
    /// keys become (id, the time column), the audit trail's chain position is indexed without being unique (the sealer and
    /// the chain-link trigger keep it unique, <c>PlatformSql</c>), and no other table may have a foreign key to these
    /// four: none does (campaign recipients and RPM alerts keep the id of a message or a reading without one), and the
    /// migration stops if one did. The model's keys are (Id, the time) too, so every UPDATE EF Core writes names the row's
    /// month and PostgreSQL reads that partition alone.
    /// </para>
    /// <para>
    /// EF Core runs a migration in one transaction, so a failure leaves the tables as they were. Every table is locked for
    /// the copy: a large audit trail takes a few minutes, during the upgrade, after the backup taken before it.
    /// <c>Down</c> makes each table plain again, the same way, with its original primary key and unique chain position.
    /// The node that runs the jobs adds the months ahead from then on (<c>PartitionMaintenance</c>).
    /// </para>
    /// </summary>
    public partial class PartitionHighVolumeTables : Migration
    {
        /// <summary>The tables, their time column, and how many months ahead the migration makes.</summary>
        internal const string Tables = """
            ('audit', 'audit_events', 'occurred_at'),
            ('virtualcare', 'rpm_readings', 'measured_at'),
            ('platform', 'notification_logs', 'created_at'),
            ('integrations', 'integration_logs', 'created_at')
            """;

        internal const int MonthsAhead = 3;

        internal const int MonthsBack = 15 * 12;

        /// <summary>
        /// How long the migration waits for each table's lock before it gives up, rolls back and stops the start-up with
        /// "canceling statement due to lock timeout". A lock held longer means something still uses the table (another
        /// HC360 server left running, or a reporting tool with a transaction open): waiting for it would hang the
        /// upgrade, and every audit write would queue behind the waiting lock meanwhile.
        /// </summary>
        internal const string LockTimeoutSql = "SET LOCAL lock_timeout = '30s'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockTimeoutSql);
            migrationBuilder.DropIndex(
                name: "ix_audit_events_chain_index",
                schema: "audit",
                table: "audit_events");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_chain_index",
                schema: "audit",
                table: "audit_events",
                column: "chain_index");

            migrationBuilder.Sql(PartitionSql);
            migrationBuilder.Sql(AuditTruncateTriggersSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockTimeoutSql);
            migrationBuilder.Sql(UnpartitionSql);

            migrationBuilder.DropIndex(
                name: "ix_audit_events_chain_index",
                schema: "audit",
                table: "audit_events");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_chain_index",
                schema: "audit",
                table: "audit_events",
                column: "chain_index",
                unique: true);
        }

        private static readonly string PartitionSql = $$"""
            DO $partition$
            DECLARE
                spec record;
                old_table regclass;
                staging text;
                target text;
                pk_name text;
                pk_columns text;
                recreate text[];
                statement text;
                this_month timestamp := date_trunc('month', now() AT TIME ZONE 'UTC');
                month timestamp;
                copied bigint;
                existing bigint;
                old_sequence text;
                new_sequence text;
                sequence_value bigint;
                sequence_called boolean;
            BEGIN
                FOR spec IN SELECT * FROM (VALUES {{Tables}}) AS t(schema_name, table_name, key_column) LOOP
                    old_table := format('%I.%I', spec.schema_name, spec.table_name)::regclass;
                    staging := format('%I.%I', spec.schema_name, spec.table_name || '__partitioned');
                    target := format('%I.%I', spec.schema_name, spec.table_name);
                    EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE', old_table);

                    IF EXISTS (SELECT 1 FROM pg_constraint WHERE contype = 'f' AND confrelid = old_table) THEN
                        RAISE EXCEPTION '% is referenced by a foreign key, which a partitioned table cannot be', old_table;
                    END IF;

                    SELECT c.conname, string_agg(quote_ident(a.attname), ', ' ORDER BY k.ord)
                      INTO pk_name, pk_columns
                      FROM pg_constraint c
                      CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
                     WHERE c.conrelid = old_table AND c.contype = 'p'
                     GROUP BY c.conname;

                    -- Made again on the new table once the old one is gone, from the old one's own definitions: its
                    -- indexes but the primary key, then its foreign keys, then its triggers.
                    SELECT array_agg(definition ORDER BY kind, definition) INTO recreate FROM (
                        SELECT 1 AS kind, pg_get_indexdef(i.indexrelid) AS definition
                          FROM pg_index i WHERE i.indrelid = old_table AND NOT i.indisprimary
                        UNION ALL
                        SELECT 2, format('ALTER TABLE %s ADD CONSTRAINT %I %s', target, c.conname, pg_get_constraintdef(c.oid))
                          FROM pg_constraint c WHERE c.conrelid = old_table AND c.contype = 'f'
                        UNION ALL
                        SELECT 3, pg_get_triggerdef(t.oid) FROM pg_trigger t WHERE t.tgrelid = old_table AND NOT t.tgisinternal) d;

                    EXECUTE format('CREATE TABLE %s (LIKE %s INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING IDENTITY INCLUDING GENERATED '
                        'INCLUDING STORAGE INCLUDING COMMENTS) PARTITION BY RANGE (%I)', staging, old_table, spec.key_column);

                    -- A partition, named <table>_YYYY_MM, for this month, the next {{MonthsAhead}} and every month in the last
                    -- {{MonthsBack}} that has rows; anything else (a device's clock years out) goes to <table>_default.
                    FOR month IN EXECUTE format(
                        'SELECT DISTINCT date_trunc(''month'', %1$I AT TIME ZONE ''UTC'') FROM %2$s WHERE %1$I >= $1 AND %1$I < $2 '
                        'UNION SELECT generate_series($3, $4, interval ''1 month'') ORDER BY 1', spec.key_column, old_table)
                        USING (this_month - make_interval(months => {{MonthsBack}})) AT TIME ZONE 'UTC', this_month AT TIME ZONE 'UTC',
                              this_month, this_month + make_interval(months => {{MonthsAhead}})
                    LOOP
                        EXECUTE format('CREATE TABLE %I.%I PARTITION OF %s FOR VALUES FROM (%L) TO (%L)',
                            spec.schema_name, spec.table_name || to_char(month, '"_"YYYY"_"MM'), staging,
                            to_char(month, 'YYYY-MM-DD') || ' 00:00:00+00', to_char(month + interval '1 month', 'YYYY-MM-DD') || ' 00:00:00+00');
                    END LOOP;
                    EXECUTE format('CREATE TABLE %I.%I PARTITION OF %s DEFAULT', spec.schema_name, spec.table_name || '_default', staging);

                    EXECUTE format('INSERT INTO %s OVERRIDING SYSTEM VALUE SELECT * FROM %s', staging, old_table);
                    GET DIAGNOSTICS copied = ROW_COUNT;
                    EXECUTE format('SELECT count(*) FROM %s', old_table) INTO existing;
                    IF copied <> existing THEN
                        RAISE EXCEPTION '% rows of % were copied, not %', copied, old_table, existing;
                    END IF;

                    -- The identity (the audit trail's id) goes on from where it was.
                    old_sequence := pg_get_serial_sequence(target, 'id');
                    IF old_sequence IS NOT NULL THEN
                        new_sequence := pg_get_serial_sequence(staging, 'id');
                        EXECUTE format('SELECT last_value, is_called FROM %s', old_sequence) INTO sequence_value, sequence_called;
                        PERFORM setval(new_sequence, sequence_value, sequence_called);
                    END IF;

                    EXECUTE format('DROP TABLE %s', old_table);
                    EXECUTE format('ALTER TABLE %s RENAME TO %I', staging, spec.table_name);
                    IF old_sequence IS NOT NULL THEN
                        EXECUTE format('ALTER SEQUENCE %s RENAME TO %I', new_sequence, (parse_ident(old_sequence))[2]);
                    END IF;

                    EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I PRIMARY KEY (%s, %I)', target, pk_name, pk_columns, spec.key_column);
                    FOREACH statement IN ARRAY coalesce(recreate, '{}') LOOP
                        EXECUTE statement;
                    END LOOP;

                    EXECUTE format('ANALYZE %s', target);
                END LOOP;
            END
            $partition$;
            """;

        // A partition inherits the audit trail's row triggers but not its statement trigger against TRUNCATE: each gets its
        // own (as PlatformSql and PartitionMaintenance do), once the trigger function exists (it does after the first start).
        private const string AuditTruncateTriggersSql = """
            DO $truncate$
            DECLARE
                leaf regclass;
            BEGIN
                IF to_regprocedure('audit.protect_audit_events()') IS NOT NULL THEN
                    FOR leaf IN SELECT relid FROM pg_partition_tree('audit.audit_events') WHERE isleaf LOOP
                        EXECUTE format('CREATE TRIGGER trg_protect_audit_events_truncate BEFORE TRUNCATE ON %s '
                            'FOR EACH STATEMENT EXECUTE FUNCTION audit.protect_audit_events()', leaf);
                    END LOOP;
                END IF;
            END
            $truncate$;
            """;

        private static readonly string UnpartitionSql = $$"""
            DO $unpartition$
            DECLARE
                spec record;
                parent regclass;
                staging text;
                target text;
                pk_name text;
                pk_columns text;
                recreate text[];
                statement text;
                copied bigint;
                existing bigint;
                old_sequence text;
                new_sequence text;
                sequence_value bigint;
                sequence_called boolean;
            BEGIN
                FOR spec IN SELECT * FROM (VALUES {{Tables}}) AS t(schema_name, table_name, key_column) LOOP
                    parent := format('%I.%I', spec.schema_name, spec.table_name)::regclass;
                    staging := format('%I.%I', spec.schema_name, spec.table_name || '__unpartitioned');
                    target := format('%I.%I', spec.schema_name, spec.table_name);
                    EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE', parent);

                    -- The primary key without the partition key: (id) again.
                    SELECT c.conname, string_agg(quote_ident(a.attname), ', ' ORDER BY k.ord) FILTER (WHERE a.attname <> spec.key_column)
                      INTO pk_name, pk_columns
                      FROM pg_constraint c
                      CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
                     WHERE c.conrelid = parent AND c.contype = 'p'
                     GROUP BY c.conname;

                    -- The partitioned table's own indexes, foreign keys and triggers (not the copies on its partitions). An
                    -- index of a partitioned table is defined ON ONLY the table.
                    SELECT array_agg(definition ORDER BY kind, definition) INTO recreate FROM (
                        SELECT 1 AS kind, replace(pg_get_indexdef(i.indexrelid), ' ON ONLY ', ' ON ') AS definition
                          FROM pg_index i WHERE i.indrelid = parent AND NOT i.indisprimary
                        UNION ALL
                        SELECT 2, format('ALTER TABLE %s ADD CONSTRAINT %I %s', target, c.conname, pg_get_constraintdef(c.oid))
                          FROM pg_constraint c WHERE c.conrelid = parent AND c.contype = 'f'
                        UNION ALL
                        SELECT 3, pg_get_triggerdef(t.oid) FROM pg_trigger t WHERE t.tgrelid = parent AND NOT t.tgisinternal) d;

                    EXECUTE format('CREATE TABLE %s (LIKE %s INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING IDENTITY INCLUDING GENERATED '
                        'INCLUDING STORAGE INCLUDING COMMENTS)', staging, parent);
                    EXECUTE format('INSERT INTO %s OVERRIDING SYSTEM VALUE SELECT * FROM %s', staging, parent);
                    GET DIAGNOSTICS copied = ROW_COUNT;
                    EXECUTE format('SELECT count(*) FROM %s', parent) INTO existing;
                    IF copied <> existing THEN
                        RAISE EXCEPTION '% rows of % were copied, not %', copied, parent, existing;
                    END IF;

                    old_sequence := pg_get_serial_sequence(target, 'id');
                    IF old_sequence IS NOT NULL THEN
                        new_sequence := pg_get_serial_sequence(staging, 'id');
                        EXECUTE format('SELECT last_value, is_called FROM %s', old_sequence) INTO sequence_value, sequence_called;
                        PERFORM setval(new_sequence, sequence_value, sequence_called);
                    END IF;

                    -- With its partitions.
                    EXECUTE format('DROP TABLE %s', parent);
                    EXECUTE format('ALTER TABLE %s RENAME TO %I', staging, spec.table_name);
                    IF old_sequence IS NOT NULL THEN
                        EXECUTE format('ALTER SEQUENCE %s RENAME TO %I', new_sequence, (parse_ident(old_sequence))[2]);
                    END IF;

                    EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I PRIMARY KEY (%s)', target, pk_name, pk_columns);
                    FOREACH statement IN ARRAY coalesce(recreate, '{}') LOOP
                        EXECUTE statement;
                    END LOOP;

                    EXECUTE format('ANALYZE %s', target);
                END LOOP;
            END
            $unpartition$;
            """;
    }
}
