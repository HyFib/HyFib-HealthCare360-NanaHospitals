using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Ambulance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ambulance_vehicles",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registration_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fitness_valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    insurance_valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    pollution_valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    odometer_km = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_ambulance_vehicles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ambulance_trips",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    patient_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    caller_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    caller_phone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pickup_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    drop_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    complaint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vehicle_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    vehicle_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    driver_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    crew_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    start_odometer_km = table.Column<int>(type: "integer", nullable: true),
                    end_odometer_km = table.Column<int>(type: "integer", nullable: true),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    at_scene_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    left_scene_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    charge_waived_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
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
                    table.PrimaryKey("pk_ambulance_trips", x => x.id);
                    table.ForeignKey(
                        name: "fk_ambulance_trips_ambulance_vehicle_vehicle_id",
                        column: x => x.vehicle_id,
                        principalSchema: "supportservices",
                        principalTable: "ambulance_vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ambulance_trips_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ambulance_trips_facility_id",
                schema: "supportservices",
                table: "ambulance_trips",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_ambulance_trips_facility_id_number",
                schema: "supportservices",
                table: "ambulance_trips",
                columns: new[] { "facility_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ambulance_trips_facility_id_status_requested_at",
                schema: "supportservices",
                table: "ambulance_trips",
                columns: new[] { "facility_id", "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ambulance_trips_is_deleted",
                schema: "supportservices",
                table: "ambulance_trips",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_ambulance_trips_patient_id",
                schema: "supportservices",
                table: "ambulance_trips",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_ambulance_trips_vehicle_id",
                schema: "supportservices",
                table: "ambulance_trips",
                column: "vehicle_id",
                unique: true,
                filter: "vehicle_id IS NOT NULL AND status IN ('Dispatched', 'AtScene', 'Transporting') AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_ambulance_vehicles_facility_id",
                schema: "supportservices",
                table: "ambulance_vehicles",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_ambulance_vehicles_is_deleted",
                schema: "supportservices",
                table: "ambulance_vehicles",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_ambulance_vehicles_registration_number",
                schema: "supportservices",
                table: "ambulance_vehicles",
                column: "registration_number",
                unique: true,
                filter: "is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ambulance_trips",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "ambulance_vehicles",
                schema: "supportservices");
        }
    }
}
