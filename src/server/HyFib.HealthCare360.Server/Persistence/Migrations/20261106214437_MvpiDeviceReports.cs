using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MvpiDeviceReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "involves_device",
                schema: "quality",
                table: "incidents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "mvpi_reports",
                schema: "quality",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    follow_up_of_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reported_on = table.Column<DateOnly>(type: "date", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    no_patient_involved = table.Column<bool>(type: "boolean", nullable: false),
                    patient_initials = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    patient_age_years = table.Column<int>(type: "integer", nullable: true),
                    patient_sex = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    weight_kg = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    event_on = table.Column<DateOnly>(type: "date", nullable: false),
                    aware_on = table.Column<DateOnly>(type: "date", nullable: false),
                    location = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    seriousness = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    death_on = table.Column<DateOnly>(type: "date", nullable: true),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device = table.Column<string>(type: "jsonb", nullable: false),
                    reported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reporter_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reporter_designation = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    reporter_department = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    submitted_on = table.Column<DateOnly>(type: "date", nullable: true),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reference_correction_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reference_corrected_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reference_corrected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_mvpi_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_mvpi_reports_incidents_incident_id",
                        column: x => x.incident_id,
                        principalSchema: "quality",
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mvpi_reports_mvpi_reports_follow_up_of_id",
                        column: x => x.follow_up_of_id,
                        principalSchema: "quality",
                        principalTable: "mvpi_reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mvpi_reports_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mvpi_reports_facility_id",
                schema: "quality",
                table: "mvpi_reports",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_mvpi_reports_facility_id_status_event_on",
                schema: "quality",
                table: "mvpi_reports",
                columns: new[] { "facility_id", "status", "event_on" });

            migrationBuilder.CreateIndex(
                name: "ix_mvpi_reports_follow_up_of_id",
                schema: "quality",
                table: "mvpi_reports",
                column: "follow_up_of_id",
                filter: "follow_up_of_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mvpi_reports_incident_id",
                schema: "quality",
                table: "mvpi_reports",
                column: "incident_id",
                filter: "incident_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mvpi_reports_is_deleted",
                schema: "quality",
                table: "mvpi_reports",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_mvpi_reports_number",
                schema: "quality",
                table: "mvpi_reports",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mvpi_reports_patient_id",
                schema: "quality",
                table: "mvpi_reports",
                column: "patient_id",
                filter: "patient_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mvpi_reports",
                schema: "quality");

            migrationBuilder.DropColumn(
                name: "involves_device",
                schema: "quality",
                table: "incidents");
        }
    }
}
