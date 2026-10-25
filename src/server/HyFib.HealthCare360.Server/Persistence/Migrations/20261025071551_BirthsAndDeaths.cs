using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BirthsAndDeaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "birth_records",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mother_patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    baby_patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    serial_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    born_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sex = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    weight_grams = table.Column<int>(type: "integer", nullable: true),
                    weeks_of_gestation = table.Column<int>(type: "integer", nullable: true),
                    method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    attendant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    child_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    father_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    mother_age_years = table.Column<int>(type: "integer", nullable: false),
                    live_children = table.Column<int>(type: "integer", nullable: true),
                    informant_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    still_birth_cause = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reported_on = table.Column<DateOnly>(type: "date", nullable: true),
                    registration_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_birth_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_birth_records_patients_baby_patient_id",
                        column: x => x.baby_patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_birth_records_patients_mother_patient_id",
                        column: x => x.mother_patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "death_records",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    serial_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    died_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    causes = table.Column<string>(type: "jsonb", nullable: false),
                    other_conditions = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    manner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    medico_legal = table.Column<bool>(type: "boolean", nullable: false),
                    pregnancy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    certified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    certifier_registration_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    certified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    certified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reported_on = table.Column<DateOnly>(type: "date", nullable: true),
                    registration_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_death_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_death_records_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_birth_records_baby_patient_id",
                schema: "clinical",
                table: "birth_records",
                column: "baby_patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_birth_records_facility_id",
                schema: "clinical",
                table: "birth_records",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_birth_records_facility_id_status_born_at",
                schema: "clinical",
                table: "birth_records",
                columns: new[] { "facility_id", "status", "born_at" });

            migrationBuilder.CreateIndex(
                name: "ix_birth_records_is_deleted",
                schema: "clinical",
                table: "birth_records",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_birth_records_mother_patient_id",
                schema: "clinical",
                table: "birth_records",
                column: "mother_patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_birth_records_serial_number",
                schema: "clinical",
                table: "birth_records",
                column: "serial_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_death_records_facility_id",
                schema: "clinical",
                table: "death_records",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_death_records_facility_id_status_died_at",
                schema: "clinical",
                table: "death_records",
                columns: new[] { "facility_id", "status", "died_at" });

            migrationBuilder.CreateIndex(
                name: "ix_death_records_is_deleted",
                schema: "clinical",
                table: "death_records",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_death_records_patient_id",
                schema: "clinical",
                table: "death_records",
                column: "patient_id",
                unique: true,
                filter: "status <> 'EnteredInError'");

            migrationBuilder.CreateIndex(
                name: "ix_death_records_serial_number",
                schema: "clinical",
                table: "death_records",
                column: "serial_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "birth_records",
                schema: "clinical");

            migrationBuilder.DropTable(
                name: "death_records",
                schema: "clinical");
        }
    }
}
