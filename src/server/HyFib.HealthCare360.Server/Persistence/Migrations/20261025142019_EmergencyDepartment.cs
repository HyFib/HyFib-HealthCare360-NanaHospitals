using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmergencyDepartment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "emergency_cases",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    arrived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    arrival_mode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    level = table.Column<int>(type: "integer", nullable: true),
                    triaged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    doctor_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    doctor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    disposition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    disposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disposed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    disposition_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    referred_to = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_emergency_cases", x => x.id);
                    table.ForeignKey(
                        name: "fk_emergency_cases_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_emergency_cases_visits_visit_id",
                        column: x => x.visit_id,
                        principalSchema: "patients",
                        principalTable: "visits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mlc_records",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    identified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    brought_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    brought_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    cause = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    incident_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    incident_place = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    injuries = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    conscious_on_arrival = table.Column<bool>(type: "boolean", nullable: true),
                    smell_of_alcohol = table.Column<bool>(type: "boolean", nullable: true),
                    doctor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    doctor_registration_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    police_station = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    intimated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    intimation_mode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    received_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    police_entry_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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
                    table.PrimaryKey("pk_mlc_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_mlc_records_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mlc_records_visits_visit_id",
                        column: x => x.visit_id,
                        principalSchema: "patients",
                        principalTable: "visits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "esi_triages",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assessed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    chief_complaint = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    needs_life_saving_intervention = table.Column<bool>(type: "boolean", nullable: false),
                    high_risk_situation = table.Column<bool>(type: "boolean", nullable: false),
                    confused_lethargic_or_disoriented = table.Column<bool>(type: "boolean", nullable: false),
                    severe_distress = table.Column<bool>(type: "boolean", nullable: false),
                    expected_resources = table.Column<int>(type: "integer", nullable: false),
                    pulse = table.Column<int>(type: "integer", nullable: true),
                    respiratory_rate = table.Column<int>(type: "integer", nullable: true),
                    sp_o2 = table.Column<int>(type: "integer", nullable: true),
                    systolic_bp = table.Column<int>(type: "integer", nullable: true),
                    diastolic_bp = table.Column<int>(type: "integer", nullable: true),
                    temperature_celsius = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    gcs = table.Column<int>(type: "integer", nullable: true),
                    pain_score = table.Column<int>(type: "integer", nullable: true),
                    computed_level = table.Column<int>(type: "integer", nullable: false),
                    reasons = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    override_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    vital_signs_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_esi_triages", x => x.id);
                    table.ForeignKey(
                        name: "fk_esi_triages_emergency_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "clinical",
                        principalTable: "emergency_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_emergency_cases_facility_id",
                schema: "clinical",
                table: "emergency_cases",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_emergency_cases_facility_id_disposition",
                schema: "clinical",
                table: "emergency_cases",
                columns: new[] { "facility_id", "disposition" });

            migrationBuilder.CreateIndex(
                name: "ix_emergency_cases_is_deleted",
                schema: "clinical",
                table: "emergency_cases",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_emergency_cases_patient_id",
                schema: "clinical",
                table: "emergency_cases",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_emergency_cases_visit_id",
                schema: "clinical",
                table: "emergency_cases",
                column: "visit_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_esi_triages_case_id",
                schema: "clinical",
                table: "esi_triages",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_esi_triages_is_deleted",
                schema: "clinical",
                table: "esi_triages",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_mlc_records_facility_id",
                schema: "clinical",
                table: "mlc_records",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_mlc_records_facility_id_identified_at",
                schema: "clinical",
                table: "mlc_records",
                columns: new[] { "facility_id", "identified_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mlc_records_facility_id_number",
                schema: "clinical",
                table: "mlc_records",
                columns: new[] { "facility_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mlc_records_is_deleted",
                schema: "clinical",
                table: "mlc_records",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_mlc_records_patient_id",
                schema: "clinical",
                table: "mlc_records",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_mlc_records_visit_id",
                schema: "clinical",
                table: "mlc_records",
                column: "visit_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "esi_triages",
                schema: "clinical");

            migrationBuilder.DropTable(
                name: "mlc_records",
                schema: "clinical");

            migrationBuilder.DropTable(
                name: "emergency_cases",
                schema: "clinical");
        }
    }
}
