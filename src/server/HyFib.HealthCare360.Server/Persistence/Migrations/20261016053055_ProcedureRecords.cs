using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcedureRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "procedure_records",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    encounter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    code_system = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    display = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    performer_practitioner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    side = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    body_site = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    outcome = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    surgery_booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_procedure_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_procedure_records_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_procedure_records_encounter_id",
                schema: "clinical",
                table: "procedure_records",
                column: "encounter_id");

            migrationBuilder.CreateIndex(
                name: "ix_procedure_records_facility_id",
                schema: "clinical",
                table: "procedure_records",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_procedure_records_is_deleted",
                schema: "clinical",
                table: "procedure_records",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_procedure_records_patient_id_performed_at",
                schema: "clinical",
                table: "procedure_records",
                columns: new[] { "patient_id", "performed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_procedure_records_surgery_booking_id",
                schema: "clinical",
                table: "procedure_records",
                column: "surgery_booking_id",
                unique: true,
                filter: "surgery_booking_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "procedure_records",
                schema: "clinical");
        }
    }
}
