using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BioMedicalWaste : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "waste_bags",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    barcode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric(9,3)", precision: 9, scale: 3, nullable: false),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    collected_by = table.Column<Guid>(type: "uuid", nullable: true),
                    collected_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    handover_id = table.Column<Guid>(type: "uuid", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
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
                    table.PrimaryKey("pk_waste_bags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "waste_handovers",
                schema: "supportservices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    handed_over_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cbwtf = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    vehicle_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    received_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    receipt_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    handed_over_by = table.Column<Guid>(type: "uuid", nullable: true),
                    handed_over_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    bag_count = table.Column<int>(type: "integer", nullable: false),
                    total_kg = table.Column<decimal>(type: "numeric(11,3)", precision: 11, scale: 3, nullable: false),
                    late_bags = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_waste_handovers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_waste_bags_barcode",
                schema: "supportservices",
                table: "waste_bags",
                column: "barcode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_waste_bags_facility_id",
                schema: "supportservices",
                table: "waste_bags",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_waste_bags_facility_id_status_collected_at",
                schema: "supportservices",
                table: "waste_bags",
                columns: new[] { "facility_id", "status", "collected_at" });

            migrationBuilder.CreateIndex(
                name: "ix_waste_bags_handover_id",
                schema: "supportservices",
                table: "waste_bags",
                column: "handover_id");

            migrationBuilder.CreateIndex(
                name: "ix_waste_bags_is_deleted",
                schema: "supportservices",
                table: "waste_bags",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_waste_handovers_facility_id",
                schema: "supportservices",
                table: "waste_handovers",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_waste_handovers_facility_id_handed_over_at",
                schema: "supportservices",
                table: "waste_handovers",
                columns: new[] { "facility_id", "handed_over_at" });

            migrationBuilder.CreateIndex(
                name: "ix_waste_handovers_is_deleted",
                schema: "supportservices",
                table: "waste_handovers",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_waste_handovers_number",
                schema: "supportservices",
                table: "waste_handovers",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "waste_bags",
                schema: "supportservices");

            migrationBuilder.DropTable(
                name: "waste_handovers",
                schema: "supportservices");
        }
    }
}
