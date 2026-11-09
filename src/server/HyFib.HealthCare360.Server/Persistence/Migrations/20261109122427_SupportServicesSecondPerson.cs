using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportServicesSecondPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "mismatched_bags",
                schema: "supportservices",
                table: "waste_handovers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "handover_weight_kg",
                schema: "supportservices",
                table: "waste_bags",
                type: "numeric(9,3)",
                precision: 9,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "imputability",
                schema: "specialtycare",
                table: "transfusion_reactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "NotAssessed");

            migrationBuilder.AddColumn<string>(
                name: "lower_reason",
                schema: "specialtycare",
                table: "transfusion_reactions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lower_requested_by_name",
                schema: "specialtycare",
                table: "transfusion_reactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lowered_by_name",
                schema: "specialtycare",
                table: "transfusion_reactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sero_change_requested_by_name",
                schema: "specialtycare",
                table: "dialysis_profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sero_changed_by_name",
                schema: "specialtycare",
                table: "dialysis_profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sero_lab_result_id",
                schema: "specialtycare",
                table: "dialysis_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cancel_requested_by",
                schema: "supportservices",
                table: "canteen_sales",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancel_requested_by_name",
                schema: "supportservices",
                table: "canteen_sales",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancelled_by_name",
                schema: "supportservices",
                table: "canteen_sales",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "refund_amount",
                schema: "supportservices",
                table: "canteen_sales",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refund_mode",
                schema: "supportservices",
                table: "canteen_sales",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "charge_waived_at",
                schema: "supportservices",
                table: "ambulance_trips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "charge_waived_by",
                schema: "supportservices",
                table: "ambulance_trips",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "charge_waived_by_name",
                schema: "supportservices",
                table: "ambulance_trips",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "waiver_requested_by_name",
                schema: "supportservices",
                table: "ambulance_trips",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cleaning_areas",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ward_id = table.Column<Guid>(type: "uuid", nullable: true),
                    risk_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    frequency = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    times_per_day = table.Column<int>(type: "integer", nullable: false),
                    week_days = table.Column<string>(type: "jsonb", nullable: false),
                    checklist = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("pk_cleaning_areas", x => x.id);
                    table.ForeignKey(
                        name: "fk_cleaning_areas_wards_ward_id",
                        column: x => x.ward_id,
                        principalSchema: "inpatient",
                        principalTable: "wards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cleaning_requests",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ward_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    checklist = table.Column<string>(type: "jsonb", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    verified_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    verification_remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_cleaning_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_cleaning_requests_wards_ward_id",
                        column: x => x.ward_id,
                        principalSchema: "inpatient",
                        principalTable: "wards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "waste_accidents",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    place = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sequence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    waste_involved = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    persons_affected = table.Column<int>(type: "integer", nullable: false),
                    effects = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    emergency_measures = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    steps_to_alleviate = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    steps_to_prevent = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    authority_informed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reported_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reported_by_designation = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
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
                    table.PrimaryKey("pk_waste_accidents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cleaning_records",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    area_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    slot = table.Column<int>(type: "integer", nullable: false),
                    cleaned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleaned_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cleaned_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    checklist = table.Column<string>(type: "jsonb", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    verified_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    verification_remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_cleaning_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_cleaning_records_cleaning_areas_area_id",
                        column: x => x.area_id,
                        principalSchema: "supportservices",
                        principalTable: "cleaning_areas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_areas_facility_id",
                schema: "supportservices",
                table: "cleaning_areas",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_areas_facility_id_code",
                schema: "supportservices",
                table: "cleaning_areas",
                columns: new[] { "facility_id", "code" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_areas_is_deleted",
                schema: "supportservices",
                table: "cleaning_areas",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_areas_ward_id",
                schema: "supportservices",
                table: "cleaning_areas",
                column: "ward_id");

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_records_area_id_date_slot",
                schema: "supportservices",
                table: "cleaning_records",
                columns: new[] { "area_id", "date", "slot" },
                unique: true,
                filter: "status <> 'Redo' AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_records_facility_id",
                schema: "supportservices",
                table: "cleaning_records",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_records_facility_id_date",
                schema: "supportservices",
                table: "cleaning_records",
                columns: new[] { "facility_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_records_is_deleted",
                schema: "supportservices",
                table: "cleaning_records",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_requests_facility_id",
                schema: "supportservices",
                table: "cleaning_requests",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_requests_facility_id_status_requested_at",
                schema: "supportservices",
                table: "cleaning_requests",
                columns: new[] { "facility_id", "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_requests_is_deleted",
                schema: "supportservices",
                table: "cleaning_requests",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_cleaning_requests_ward_id",
                schema: "supportservices",
                table: "cleaning_requests",
                column: "ward_id");

            migrationBuilder.CreateIndex(
                name: "ix_waste_accidents_facility_id",
                schema: "supportservices",
                table: "waste_accidents",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_waste_accidents_facility_id_occurred_at",
                schema: "supportservices",
                table: "waste_accidents",
                columns: new[] { "facility_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_waste_accidents_is_deleted",
                schema: "supportservices",
                table: "waste_accidents",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cleaning_records",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "cleaning_requests",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "waste_accidents",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "cleaning_areas",
                schema: "supportservices");

            migrationBuilder.DropColumn(
                name: "mismatched_bags",
                schema: "supportservices",
                table: "waste_handovers");

            migrationBuilder.DropColumn(
                name: "handover_weight_kg",
                schema: "supportservices",
                table: "waste_bags");

            migrationBuilder.DropColumn(
                name: "imputability",
                schema: "specialtycare",
                table: "transfusion_reactions");

            migrationBuilder.DropColumn(
                name: "lower_reason",
                schema: "specialtycare",
                table: "transfusion_reactions");

            migrationBuilder.DropColumn(
                name: "lower_requested_by_name",
                schema: "specialtycare",
                table: "transfusion_reactions");

            migrationBuilder.DropColumn(
                name: "lowered_by_name",
                schema: "specialtycare",
                table: "transfusion_reactions");

            migrationBuilder.DropColumn(
                name: "sero_change_requested_by_name",
                schema: "specialtycare",
                table: "dialysis_profiles");

            migrationBuilder.DropColumn(
                name: "sero_changed_by_name",
                schema: "specialtycare",
                table: "dialysis_profiles");

            migrationBuilder.DropColumn(
                name: "sero_lab_result_id",
                schema: "specialtycare",
                table: "dialysis_profiles");

            migrationBuilder.DropColumn(
                name: "cancel_requested_by",
                schema: "supportservices",
                table: "canteen_sales");

            migrationBuilder.DropColumn(
                name: "cancel_requested_by_name",
                schema: "supportservices",
                table: "canteen_sales");

            migrationBuilder.DropColumn(
                name: "cancelled_by_name",
                schema: "supportservices",
                table: "canteen_sales");

            migrationBuilder.DropColumn(
                name: "refund_amount",
                schema: "supportservices",
                table: "canteen_sales");

            migrationBuilder.DropColumn(
                name: "refund_mode",
                schema: "supportservices",
                table: "canteen_sales");

            migrationBuilder.DropColumn(
                name: "charge_waived_at",
                schema: "supportservices",
                table: "ambulance_trips");

            migrationBuilder.DropColumn(
                name: "charge_waived_by",
                schema: "supportservices",
                table: "ambulance_trips");

            migrationBuilder.DropColumn(
                name: "charge_waived_by_name",
                schema: "supportservices",
                table: "ambulance_trips");

            migrationBuilder.DropColumn(
                name: "waiver_requested_by_name",
                schema: "supportservices",
                table: "ambulance_trips");
        }
    }
}
