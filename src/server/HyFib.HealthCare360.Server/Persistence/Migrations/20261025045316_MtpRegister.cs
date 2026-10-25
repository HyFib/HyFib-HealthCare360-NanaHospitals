using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MtpRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mtp_cases",
                schema: "specialtycare",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    serial_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    approval_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    age_years = table.Column<int>(type: "integer", nullable: false),
                    mentally_ill = table.Column<bool>(type: "boolean", nullable: false),
                    consent_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    guardian_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    guardian_relation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    consent_on = table.Column<DateOnly>(type: "date", nullable: false),
                    consent_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    weeks_of_gestation = table.Column<int>(type: "integer", nullable: false),
                    grounds = table.Column<int[]>(type: "integer[]", nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    medical_board_reference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    opinions = table.Column<string>(type: "jsonb", nullable: false),
                    certified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    certified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    terminated_on = table.Column<DateOnly>(type: "date", nullable: true),
                    method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    performed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    discharged_on = table.Column<DateOnly>(type: "date", nullable: true),
                    outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    remarks = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    termination_recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_mtp_cases", x => x.id);
                    table.ForeignKey(
                        name: "fk_mtp_cases_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mtp_cases_facility_id",
                schema: "specialtycare",
                table: "mtp_cases",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_mtp_cases_facility_id_status_terminated_on",
                schema: "specialtycare",
                table: "mtp_cases",
                columns: new[] { "facility_id", "status", "terminated_on" });

            migrationBuilder.CreateIndex(
                name: "ix_mtp_cases_is_deleted",
                schema: "specialtycare",
                table: "mtp_cases",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_mtp_cases_patient_id",
                schema: "specialtycare",
                table: "mtp_cases",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_mtp_cases_serial_number",
                schema: "specialtycare",
                table: "mtp_cases",
                column: "serial_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mtp_cases",
                schema: "specialtycare");
        }
    }
}
