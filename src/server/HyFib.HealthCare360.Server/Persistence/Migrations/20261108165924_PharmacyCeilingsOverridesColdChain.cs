using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PharmacyCeilingsOverridesColdChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "override_acknowledged_at",
                schema: "pharmacy",
                table: "queue_lines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_acknowledged_by_name",
                schema: "pharmacy",
                table: "queue_lines",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "override_acknowledged_by_user_id",
                schema: "pharmacy",
                table: "queue_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_acknowledgement_note",
                schema: "pharmacy",
                table: "queue_lines",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "safety_override",
                schema: "pharmacy",
                table: "queue_lines",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "safety_override_reason",
                schema: "pharmacy",
                table: "queue_entries",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "signed_by_user_id",
                schema: "pharmacy",
                table: "queue_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ceiling_notification",
                schema: "supplychain",
                table: "items",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ceiling_price",
                schema: "supplychain",
                table: "goods_receipt_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cold_storage_units",
                schema: "supplychain",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    min_celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    max_celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    logger_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    logger_key_issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_reading_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    last_logger_reading_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    open_excursion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    missed_alerted_through = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    logger_silence_alerted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_cold_storage_units", x => x.id);
                    table.ForeignKey(
                        name: "fk_cold_storage_units_store_store_id",
                        column: x => x.store_id,
                        principalSchema: "supplychain",
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cold_chain_excursions",
                schema: "supplychain",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lowest_celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    highest_celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    readings_out_of_range = table.Column<int>(type: "integer", nullable: false),
                    min_celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    max_celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    items_at_risk = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    action_taken = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    action_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    action_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cold_chain_excursions", x => x.id);
                    table.ForeignKey(
                        name: "fk_cold_chain_excursions_cold_storage_unit_unit_id",
                        column: x => x.unit_id,
                        principalSchema: "supplychain",
                        principalTable: "cold_storage_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "temperature_readings",
                schema: "supplychain",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    min_celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    max_celsius = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    out_of_range = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_temperature_readings", x => x.id);
                    table.ForeignKey(
                        name: "fk_temperature_readings_cold_storage_units_unit_id",
                        column: x => x.unit_id,
                        principalSchema: "supplychain",
                        principalTable: "cold_storage_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cold_chain_excursions_facility_id",
                schema: "supplychain",
                table: "cold_chain_excursions",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_cold_chain_excursions_unit_id_started_at",
                schema: "supplychain",
                table: "cold_chain_excursions",
                columns: new[] { "unit_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cold_storage_units_facility_id",
                schema: "supplychain",
                table: "cold_storage_units",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_cold_storage_units_facility_id_code",
                schema: "supplychain",
                table: "cold_storage_units",
                columns: new[] { "facility_id", "code" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_cold_storage_units_is_deleted",
                schema: "supplychain",
                table: "cold_storage_units",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_cold_storage_units_store_id",
                schema: "supplychain",
                table: "cold_storage_units",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ix_temperature_readings_facility_id",
                schema: "supplychain",
                table: "temperature_readings",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_temperature_readings_unit_id_taken_at",
                schema: "supplychain",
                table: "temperature_readings",
                columns: new[] { "unit_id", "taken_at" });

            migrationBuilder.CreateIndex(
                name: "ix_temperature_readings_unit_id_taken_at_source",
                schema: "supplychain",
                table: "temperature_readings",
                columns: new[] { "unit_id", "taken_at", "source" },
                unique: true,
                filter: "source = 'Logger'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cold_chain_excursions",
                schema: "supplychain");

            migrationBuilder.DropTable(
                name: "temperature_readings",
                schema: "supplychain");

            migrationBuilder.DropTable(
                name: "cold_storage_units",
                schema: "supplychain");

            migrationBuilder.DropColumn(
                name: "override_acknowledged_at",
                schema: "pharmacy",
                table: "queue_lines");

            migrationBuilder.DropColumn(
                name: "override_acknowledged_by_name",
                schema: "pharmacy",
                table: "queue_lines");

            migrationBuilder.DropColumn(
                name: "override_acknowledged_by_user_id",
                schema: "pharmacy",
                table: "queue_lines");

            migrationBuilder.DropColumn(
                name: "override_acknowledgement_note",
                schema: "pharmacy",
                table: "queue_lines");

            migrationBuilder.DropColumn(
                name: "safety_override",
                schema: "pharmacy",
                table: "queue_lines");

            migrationBuilder.DropColumn(
                name: "safety_override_reason",
                schema: "pharmacy",
                table: "queue_entries");

            migrationBuilder.DropColumn(
                name: "signed_by_user_id",
                schema: "pharmacy",
                table: "queue_entries");

            migrationBuilder.DropColumn(
                name: "ceiling_notification",
                schema: "supplychain",
                table: "items");

            migrationBuilder.DropColumn(
                name: "ceiling_price",
                schema: "supplychain",
                table: "goods_receipt_lines");
        }
    }
}
