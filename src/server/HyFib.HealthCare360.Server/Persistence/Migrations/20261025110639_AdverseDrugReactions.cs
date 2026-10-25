using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdverseDrugReactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adr_reports",
                schema: "pharmacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    learned_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reaction = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    reaction_started_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reaction_stopped_on = table.Column<DateOnly>(type: "date", nullable: true),
                    seriousness = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    relevant_tests = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    medical_history = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    drugs = table.Column<string>(type: "jsonb", nullable: false),
                    reported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reporter_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reporter_designation = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    causality = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    assessed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    submitted_on = table.Column<DateOnly>(type: "date", nullable: true),
                    submission_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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
                    table.PrimaryKey("pk_adr_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_adr_reports_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_adr_reports_facility_id",
                schema: "pharmacy",
                table: "adr_reports",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_adr_reports_facility_id_status_learned_on",
                schema: "pharmacy",
                table: "adr_reports",
                columns: new[] { "facility_id", "status", "learned_on" });

            migrationBuilder.CreateIndex(
                name: "ix_adr_reports_is_deleted",
                schema: "pharmacy",
                table: "adr_reports",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_adr_reports_number",
                schema: "pharmacy",
                table: "adr_reports",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_adr_reports_patient_id",
                schema: "pharmacy",
                table: "adr_reports",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adr_reports",
                schema: "pharmacy");
        }
    }
}
