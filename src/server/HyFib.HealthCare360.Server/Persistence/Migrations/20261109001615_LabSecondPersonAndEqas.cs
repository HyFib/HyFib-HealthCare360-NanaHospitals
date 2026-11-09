using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabSecondPersonAndEqas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "covering_reason",
                schema: "clinical",
                table: "result_reviews",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approved_by",
                schema: "diagnostics",
                table: "qc_overrides",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "lab_result_ids",
                schema: "diagnostics",
                table: "qc_overrides",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "eqas_schemes",
                schema: "diagnostics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    participant_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    rounds_per_year = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_eqas_schemes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eqas_rounds",
                schema: "diagnostics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lab_test_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    sample_received_on = table.Column<DateOnly>(type: "date", nullable: true),
                    reported_value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    reported_on = table.Column<DateOnly>(type: "date", nullable: true),
                    reported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    assigned_sd = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    peer_group = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    evaluated_on = table.Column<DateOnly>(type: "date", nullable: true),
                    z_score = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    performance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    capa_id = table.Column<Guid>(type: "uuid", nullable: true),
                    capa_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    acceptance_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
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
                    table.PrimaryKey("pk_eqas_rounds", x => x.id);
                    table.ForeignKey(
                        name: "fk_eqas_rounds_eqas_scheme_scheme_id",
                        column: x => x.scheme_id,
                        principalSchema: "diagnostics",
                        principalTable: "eqas_schemes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_eqas_rounds_lab_test_lab_test_id",
                        column: x => x.lab_test_id,
                        principalSchema: "diagnostics",
                        principalTable: "lab_tests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_eqas_rounds_facility_id",
                schema: "diagnostics",
                table: "eqas_rounds",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_eqas_rounds_facility_id_due_date",
                schema: "diagnostics",
                table: "eqas_rounds",
                columns: new[] { "facility_id", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_eqas_rounds_is_deleted",
                schema: "diagnostics",
                table: "eqas_rounds",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_eqas_rounds_lab_test_id",
                schema: "diagnostics",
                table: "eqas_rounds",
                column: "lab_test_id");

            migrationBuilder.CreateIndex(
                name: "ix_eqas_rounds_scheme_id_lab_test_id_round_code",
                schema: "diagnostics",
                table: "eqas_rounds",
                columns: new[] { "scheme_id", "lab_test_id", "round_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eqas_schemes_facility_id",
                schema: "diagnostics",
                table: "eqas_schemes",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_eqas_schemes_facility_id_name",
                schema: "diagnostics",
                table: "eqas_schemes",
                columns: new[] { "facility_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eqas_schemes_is_deleted",
                schema: "diagnostics",
                table: "eqas_schemes",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eqas_rounds",
                schema: "diagnostics");

            migrationBuilder.DropTable(
                name: "eqas_schemes",
                schema: "diagnostics");

            migrationBuilder.DropColumn(
                name: "covering_reason",
                schema: "clinical",
                table: "result_reviews");

            migrationBuilder.DropColumn(
                name: "approved_by",
                schema: "diagnostics",
                table: "qc_overrides");

            migrationBuilder.DropColumn(
                name: "lab_result_ids",
                schema: "diagnostics",
                table: "qc_overrides");
        }
    }
}
