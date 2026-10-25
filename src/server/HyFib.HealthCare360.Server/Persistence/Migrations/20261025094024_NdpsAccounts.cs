using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NdpsAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "narcotic_counts",
                schema: "pharmacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    counted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    counted_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    witnessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    witnessed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    witnessed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    explanation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
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
                    table.PrimaryKey("pk_narcotic_counts", x => x.id);
                    table.ForeignKey(
                        name: "fk_narcotic_counts_stores_store_id",
                        column: x => x.store_id,
                        principalSchema: "supplychain",
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "narcotic_count_lines",
                schema: "pharmacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    narcotic_count_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    register_balance = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    counted = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_narcotic_count_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_narcotic_count_lines_narcotic_counts_narcotic_count_id",
                        column: x => x.narcotic_count_id,
                        principalSchema: "pharmacy",
                        principalTable: "narcotic_counts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_narcotic_count_lines_narcotic_count_id",
                schema: "pharmacy",
                table: "narcotic_count_lines",
                column: "narcotic_count_id");

            migrationBuilder.CreateIndex(
                name: "ix_narcotic_counts_facility_id",
                schema: "pharmacy",
                table: "narcotic_counts",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_narcotic_counts_facility_id_store_id_counted_at",
                schema: "pharmacy",
                table: "narcotic_counts",
                columns: new[] { "facility_id", "store_id", "counted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_narcotic_counts_is_deleted",
                schema: "pharmacy",
                table: "narcotic_counts",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_narcotic_counts_store_id",
                schema: "pharmacy",
                table: "narcotic_counts",
                column: "store_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "narcotic_count_lines",
                schema: "pharmacy");

            migrationBuilder.DropTable(
                name: "narcotic_counts",
                schema: "pharmacy");
        }
    }
}
