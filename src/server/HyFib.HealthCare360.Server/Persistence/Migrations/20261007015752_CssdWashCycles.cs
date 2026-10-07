using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CssdWashCycles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "wash_cycle_id",
                schema: "supportservices",
                table: "sterile_packs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "wash_cycles",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    washer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    program = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disinfection_temperature_c = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    hold_minutes = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    a0 = table.Column<int>(type: "integer", nullable: true),
                    inspected_clean = table.Column<bool>(type: "boolean", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    operator_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    operator_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_wash_cycles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wash_cycle_trays",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wash_cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wash_cycle_trays", x => x.id);
                    table.ForeignKey(
                        name: "fk_wash_cycle_trays_instrument_sets_instrument_set_id",
                        column: x => x.instrument_set_id,
                        principalSchema: "supportservices",
                        principalTable: "instrument_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wash_cycle_trays_wash_cycles_wash_cycle_id",
                        column: x => x.wash_cycle_id,
                        principalSchema: "supportservices",
                        principalTable: "wash_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sterile_packs_wash_cycle_id",
                schema: "supportservices",
                table: "sterile_packs",
                column: "wash_cycle_id");

            migrationBuilder.CreateIndex(
                name: "ix_wash_cycle_trays_instrument_set_id_wash_cycle_id",
                schema: "supportservices",
                table: "wash_cycle_trays",
                columns: new[] { "instrument_set_id", "wash_cycle_id" });

            migrationBuilder.CreateIndex(
                name: "ix_wash_cycle_trays_wash_cycle_id",
                schema: "supportservices",
                table: "wash_cycle_trays",
                column: "wash_cycle_id");

            migrationBuilder.CreateIndex(
                name: "ix_wash_cycles_cycle_number",
                schema: "supportservices",
                table: "wash_cycles",
                column: "cycle_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wash_cycles_facility_id",
                schema: "supportservices",
                table: "wash_cycles",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_wash_cycles_is_deleted",
                schema: "supportservices",
                table: "wash_cycles",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_wash_cycles_status_started_at",
                schema: "supportservices",
                table: "wash_cycles",
                columns: new[] { "status", "started_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_sterile_packs_wash_cycle_wash_cycle_id",
                schema: "supportservices",
                table: "sterile_packs",
                column: "wash_cycle_id",
                principalSchema: "supportservices",
                principalTable: "wash_cycles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_sterile_packs_wash_cycle_wash_cycle_id",
                schema: "supportservices",
                table: "sterile_packs");

            migrationBuilder.DropTable(
                name: "wash_cycle_trays",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "wash_cycles",
                schema: "supportservices");

            migrationBuilder.DropIndex(
                name: "ix_sterile_packs_wash_cycle_id",
                schema: "supportservices",
                table: "sterile_packs");

            migrationBuilder.DropColumn(
                name: "wash_cycle_id",
                schema: "supportservices",
                table: "sterile_packs");
        }
    }
}
