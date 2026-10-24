using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CertInAndAuditSeals : Migration
    {
        /// <summary>The controlled-drug registers: the pharmacy's (every movement) and the prescriber's (every signed controlled line).</summary>
        internal static readonly string[] Registers = ["pharmacy.controlled_drug_register", "clinical.controlled_drug_register"];

        internal static readonly string RegisterGuard = """
            CREATE OR REPLACE FUNCTION audit.protect_controlled_drug_register() RETURNS trigger AS $$
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                    RAISE EXCEPTION '%.% is append-only: register lines are never deleted', TG_TABLE_SCHEMA, TG_TABLE_NAME;
                END IF;
                IF (to_jsonb(NEW) - 'patient_id') IS DISTINCT FROM (to_jsonb(OLD) - 'patient_id') THEN
                    RAISE EXCEPTION '%.% is append-only: only a merge of patient records may move a line''s patient link', TG_TABLE_SCHEMA, TG_TABLE_NAME;
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            """ + string.Concat(Registers.Select(table => $"""
            CREATE OR REPLACE TRIGGER trg_protect_controlled_drug_register BEFORE DELETE OR UPDATE ON {table}
                FOR EACH ROW EXECUTE FUNCTION audit.protect_controlled_drug_register();
            CREATE OR REPLACE TRIGGER trg_protect_controlled_drug_register_truncate BEFORE TRUNCATE ON {table}
                FOR EACH STATEMENT EXECUTE FUNCTION audit.protect_controlled_drug_register();

            """));

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cert_in_reference",
                schema: "privacy",
                table: "breaches",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cert_in_reported_at",
                schema: "privacy",
                table: "breaches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cert_in_type",
                schema: "privacy",
                table: "breaches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mac",
                schema: "audit",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // The controlled-drug registers are append-only (D&C Rules, NDPS Rules): no line is deleted, and none changes but its
            // patient link, which a merge of duplicate records moves to the surviving record.
            migrationBuilder.Sql(RegisterGuard);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Registers)
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_protect_controlled_drug_register ON {table};");
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_protect_controlled_drug_register_truncate ON {table};");
            }

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit.protect_controlled_drug_register();");

            migrationBuilder.DropColumn(
                name: "cert_in_reference",
                schema: "privacy",
                table: "breaches");

            migrationBuilder.DropColumn(
                name: "cert_in_reported_at",
                schema: "privacy",
                table: "breaches");

            migrationBuilder.DropColumn(
                name: "cert_in_type",
                schema: "privacy",
                table: "breaches");

            migrationBuilder.DropColumn(
                name: "mac",
                schema: "audit",
                table: "audit_events");
        }
    }
}
