using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClinicalBaysWoundsSignOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "signer_revocation_status",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bay_assigned_at",
                schema: "clinical",
                table: "emergency_cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "bay_id",
                schema: "clinical",
                table: "emergency_cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "doctor_handovers",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_practitioner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_practitioner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_doctor_handovers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "er_bays",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    area = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_er_bays", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wound_certificates",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mlc_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    examined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    identification_marks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    history = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    injuries = table.Column<string>(type: "jsonb", nullable: false),
                    remarks = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    signed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    signed_by_practitioner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    doctor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    doctor_registration = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
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
                    table.PrimaryKey("pk_wound_certificates", x => x.id);
                    table.ForeignKey(
                        name: "fk_wound_certificates_mlc_records_mlc_record_id",
                        column: x => x.mlc_record_id,
                        principalSchema: "clinical",
                        principalTable: "mlc_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wound_certificates_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_emergency_cases_bay_id",
                schema: "clinical",
                table: "emergency_cases",
                column: "bay_id",
                unique: true,
                filter: "bay_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_doctor_handovers_facility_id",
                schema: "clinical",
                table: "doctor_handovers",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_doctor_handovers_from_practitioner_id_to_practitioner_id_un",
                schema: "clinical",
                table: "doctor_handovers",
                columns: new[] { "from_practitioner_id", "to_practitioner_id", "until" });

            migrationBuilder.CreateIndex(
                name: "ix_doctor_handovers_is_deleted",
                schema: "clinical",
                table: "doctor_handovers",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_doctor_handovers_to_practitioner_id_until",
                schema: "clinical",
                table: "doctor_handovers",
                columns: new[] { "to_practitioner_id", "until" });

            migrationBuilder.CreateIndex(
                name: "ix_er_bays_facility_id",
                schema: "clinical",
                table: "er_bays",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_er_bays_facility_id_code",
                schema: "clinical",
                table: "er_bays",
                columns: new[] { "facility_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_er_bays_is_deleted",
                schema: "clinical",
                table: "er_bays",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_wound_certificates_facility_id",
                schema: "clinical",
                table: "wound_certificates",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_wound_certificates_is_deleted",
                schema: "clinical",
                table: "wound_certificates",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_wound_certificates_mlc_record_id",
                schema: "clinical",
                table: "wound_certificates",
                column: "mlc_record_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wound_certificates_patient_id",
                schema: "clinical",
                table: "wound_certificates",
                column: "patient_id");

            migrationBuilder.AddForeignKey(
                name: "fk_emergency_cases_er_bay_bay_id",
                schema: "clinical",
                table: "emergency_cases",
                column: "bay_id",
                principalSchema: "clinical",
                principalTable: "er_bays",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_emergency_cases_er_bay_bay_id",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropTable(
                name: "doctor_handovers",
                schema: "clinical");

            migrationBuilder.DropTable(
                name: "er_bays",
                schema: "clinical");

            migrationBuilder.DropTable(
                name: "wound_certificates",
                schema: "clinical");

            migrationBuilder.DropIndex(
                name: "ix_emergency_cases_bay_id",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropColumn(
                name: "signer_revocation_status",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "bay_assigned_at",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropColumn(
                name: "bay_id",
                schema: "clinical",
                table: "emergency_cases");
        }
    }
}
