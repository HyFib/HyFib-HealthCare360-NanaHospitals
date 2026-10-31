using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <summary>
    /// What an analyser said with a result (its unit, flags, status and comment) and why the result was not
    /// auto-validated, on <c>diagnostics.lab_results</c>; results written before this had none, so their reasons start
    /// empty. And <c>diagnostics.lab_unmatched_results</c>: the analyser results that could not go on a sample's test,
    /// kept for the lab to assign or discard where they were refused, or dropped, before.
    /// </summary>
    public partial class AnalyserReadingsAndUnmatchedResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "held_reasons",
                schema: "diagnostics",
                table: "lab_results",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "instrument_comment",
                schema: "diagnostics",
                table: "lab_results",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "instrument_flags",
                schema: "diagnostics",
                table: "lab_results",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "instrument_status",
                schema: "diagnostics",
                table: "lab_results",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "instrument_unit",
                schema: "diagnostics",
                table: "lab_results",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lab_unmatched_results",
                schema: "diagnostics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sample_barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sample_id = table.Column<Guid>(type: "uuid", nullable: true),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lab_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    instrument_unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    instrument_flags = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    instrument_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    instrument_comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    resulted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    assigned_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    discard_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lab_unmatched_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_lab_unmatched_results_lab_samples_sample_id",
                        column: x => x.sample_id,
                        principalSchema: "diagnostics",
                        principalTable: "lab_samples",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lab_unmatched_results_facility_id",
                schema: "diagnostics",
                table: "lab_unmatched_results",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_lab_unmatched_results_facility_id_status_received_at",
                schema: "diagnostics",
                table: "lab_unmatched_results",
                columns: new[] { "facility_id", "status", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_lab_unmatched_results_instrument_code_sample_barcode",
                schema: "diagnostics",
                table: "lab_unmatched_results",
                columns: new[] { "instrument_code", "sample_barcode" });

            migrationBuilder.CreateIndex(
                name: "ix_lab_unmatched_results_is_deleted",
                schema: "diagnostics",
                table: "lab_unmatched_results",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_lab_unmatched_results_sample_id",
                schema: "diagnostics",
                table: "lab_unmatched_results",
                column: "sample_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_unmatched_results",
                schema: "diagnostics");

            migrationBuilder.DropColumn(
                name: "held_reasons",
                schema: "diagnostics",
                table: "lab_results");

            migrationBuilder.DropColumn(
                name: "instrument_comment",
                schema: "diagnostics",
                table: "lab_results");

            migrationBuilder.DropColumn(
                name: "instrument_flags",
                schema: "diagnostics",
                table: "lab_results");

            migrationBuilder.DropColumn(
                name: "instrument_status",
                schema: "diagnostics",
                table: "lab_results");

            migrationBuilder.DropColumn(
                name: "instrument_unit",
                schema: "diagnostics",
                table: "lab_results");
        }
    }
}
