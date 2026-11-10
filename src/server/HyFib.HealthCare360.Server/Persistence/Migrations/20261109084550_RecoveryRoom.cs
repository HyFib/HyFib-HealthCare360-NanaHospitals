using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryRoom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "recovery_destination",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "recovery_discharged_at",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "timestamp with time zone",
                nullable: true);

            // Operations completed before the recovery room was recorded left it when they left the theatre: they are not
            // held in recovery now (the discharge would wait for them, and the ward would never see the patient back).
            migrationBuilder.Sql(
                "UPDATE specialtycare.surgery_bookings SET recovery_discharged_at = completed_at WHERE completed_at IS NOT NULL;");

            migrationBuilder.CreateTable(
                name: "recovery_records",
                schema: "specialtycare",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    surgery_booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    airway = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    oxygen = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    oxygen_litres_per_minute = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    pre_anaesthesia_systolic = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    handover_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    handed_over_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    handed_over_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    handover_confirmed_at_screen = table.Column<bool>(type: "boolean", nullable: false),
                    handover_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    observations = table.Column<string>(type: "jsonb", nullable: false),
                    discharged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    destination = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    discharge_score = table.Column<int>(type: "integer", nullable: true),
                    discharged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    discharged_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    override_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    override_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    override_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_recovery_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_recovery_records_surgery_booking_surgery_booking_id",
                        column: x => x.surgery_booking_id,
                        principalSchema: "specialtycare",
                        principalTable: "surgery_bookings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_recovery_records_facility_id",
                schema: "specialtycare",
                table: "recovery_records",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_records_is_deleted",
                schema: "specialtycare",
                table: "recovery_records",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_records_surgery_booking_id",
                schema: "specialtycare",
                table: "recovery_records",
                column: "surgery_booking_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recovery_records",
                schema: "specialtycare");

            migrationBuilder.DropColumn(
                name: "recovery_destination",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "recovery_discharged_at",
                schema: "specialtycare",
                table: "surgery_bookings");
        }
    }
}
