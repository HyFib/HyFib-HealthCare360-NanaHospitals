using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KpiTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kpi_targets",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kpi_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    amber_threshold = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    red_threshold = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    owner_role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    set_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_kpi_targets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kpi_targets_facility_id",
                schema: "analytics",
                table: "kpi_targets",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_kpi_targets_facility_id_kpi_key",
                schema: "analytics",
                table: "kpi_targets",
                columns: new[] { "facility_id", "kpi_key" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_kpi_targets_is_deleted",
                schema: "analytics",
                table: "kpi_targets",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kpi_targets",
                schema: "analytics");
        }
    }
}
