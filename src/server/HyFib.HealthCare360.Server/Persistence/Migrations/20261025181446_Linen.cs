using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Linen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "linen_items",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    life_washes = table.Column<int>(type: "integer", nullable: false),
                    washes = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_linen_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "linen_movements",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ward_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ward_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    infected = table.Column<bool>(type: "boolean", nullable: false),
                    process = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    temperature_c = table.Column<int>(type: "integer", nullable: true),
                    hold_minutes = table.Column<int>(type: "integer", nullable: true),
                    condemn_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
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
                    table.PrimaryKey("pk_linen_movements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "linen_holdings",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linen_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ward_id = table.Column<Guid>(type: "uuid", nullable: true),
                    infected = table.Column<bool>(type: "boolean", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    par = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_linen_holdings", x => x.id);
                    table.CheckConstraint("ck_linen_holdings_quantity", "quantity >= 0");
                    table.ForeignKey(
                        name: "fk_linen_holdings_linen_item_linen_item_id",
                        column: x => x.linen_item_id,
                        principalSchema: "supportservices",
                        principalTable: "linen_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_linen_holdings_wards_ward_id",
                        column: x => x.ward_id,
                        principalSchema: "inpatient",
                        principalTable: "wards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "linen_movement_lines",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    linen_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linen_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_linen_movement_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_linen_movement_lines_linen_items_linen_item_id",
                        column: x => x.linen_item_id,
                        principalSchema: "supportservices",
                        principalTable: "linen_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_linen_movement_lines_linen_movements_linen_movement_id",
                        column: x => x.linen_movement_id,
                        principalSchema: "supportservices",
                        principalTable: "linen_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_linen_holdings_facility_id",
                schema: "supportservices",
                table: "linen_holdings",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_linen_holdings_facility_id_linen_item_id_location_ward_id_i",
                schema: "supportservices",
                table: "linen_holdings",
                columns: new[] { "facility_id", "linen_item_id", "location", "ward_id", "infected" },
                unique: true,
                filter: "is_deleted = false")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_linen_holdings_is_deleted",
                schema: "supportservices",
                table: "linen_holdings",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_linen_holdings_linen_item_id",
                schema: "supportservices",
                table: "linen_holdings",
                column: "linen_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_linen_holdings_ward_id",
                schema: "supportservices",
                table: "linen_holdings",
                column: "ward_id");

            migrationBuilder.CreateIndex(
                name: "ix_linen_items_facility_id",
                schema: "supportservices",
                table: "linen_items",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_linen_items_facility_id_code",
                schema: "supportservices",
                table: "linen_items",
                columns: new[] { "facility_id", "code" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_linen_items_is_deleted",
                schema: "supportservices",
                table: "linen_items",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_linen_movement_lines_linen_item_id",
                schema: "supportservices",
                table: "linen_movement_lines",
                column: "linen_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_linen_movement_lines_linen_movement_id",
                schema: "supportservices",
                table: "linen_movement_lines",
                column: "linen_movement_id");

            migrationBuilder.CreateIndex(
                name: "ix_linen_movements_facility_id",
                schema: "supportservices",
                table: "linen_movements",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_linen_movements_facility_id_at",
                schema: "supportservices",
                table: "linen_movements",
                columns: new[] { "facility_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_linen_movements_facility_id_number",
                schema: "supportservices",
                table: "linen_movements",
                columns: new[] { "facility_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_linen_movements_is_deleted",
                schema: "supportservices",
                table: "linen_movements",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "linen_holdings",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "linen_movement_lines",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "linen_items",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "linen_movements",
                schema: "supportservices");
        }
    }
}
