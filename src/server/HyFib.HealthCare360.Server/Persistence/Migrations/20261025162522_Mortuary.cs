using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Mortuary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mortuary_slots",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    chamber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
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
                    table.PrimaryKey("pk_mortuary_slots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "body_records",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    identified = table.Column<bool>(type: "boolean", nullable: false),
                    sex = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    age_years = table.Column<int>(type: "integer", nullable: true),
                    died_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    brought_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_mlc = table.Column<bool>(type: "boolean", nullable: false),
                    mlc_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    police_station = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    identification_marks = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    belongings = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    slot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slot_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_to = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    received_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    relationship = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    id_proof = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    police_clearance = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    belongings_handed_over = table.Column<bool>(type: "boolean", nullable: false),
                    release_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    released_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_body_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_body_records_mortuary_slot_slot_id",
                        column: x => x.slot_id,
                        principalSchema: "supportservices",
                        principalTable: "mortuary_slots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_body_records_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_body_records_facility_id",
                schema: "supportservices",
                table: "body_records",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_body_records_facility_id_number",
                schema: "supportservices",
                table: "body_records",
                columns: new[] { "facility_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_body_records_facility_id_status_received_at",
                schema: "supportservices",
                table: "body_records",
                columns: new[] { "facility_id", "status", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_body_records_is_deleted",
                schema: "supportservices",
                table: "body_records",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_body_records_patient_id",
                schema: "supportservices",
                table: "body_records",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_body_records_slot_id",
                schema: "supportservices",
                table: "body_records",
                column: "slot_id",
                unique: true,
                filter: "slot_id IS NOT NULL AND status = 'InStorage' AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_mortuary_slots_facility_id",
                schema: "supportservices",
                table: "mortuary_slots",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_mortuary_slots_facility_id_code",
                schema: "supportservices",
                table: "mortuary_slots",
                columns: new[] { "facility_id", "code" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_mortuary_slots_is_deleted",
                schema: "supportservices",
                table: "mortuary_slots",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "body_records",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "mortuary_slots",
                schema: "supportservices");
        }
    }
}
