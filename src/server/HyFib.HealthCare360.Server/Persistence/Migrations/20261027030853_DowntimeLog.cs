using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DowntimeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "downtime_events",
                schema: "quality",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cause = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    affected = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recorded_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    actions_taken = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ended_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    back_entry_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paper_records = table.Column<int>(type: "integer", nullable: true),
                    back_entry_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    back_entry_signed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_downtime_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_downtime_events_facility_id",
                schema: "quality",
                table: "downtime_events",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_downtime_events_facility_id_started_at",
                schema: "quality",
                table: "downtime_events",
                columns: new[] { "facility_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_downtime_events_is_deleted",
                schema: "quality",
                table: "downtime_events",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_downtime_events_number",
                schema: "quality",
                table: "downtime_events",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "downtime_events",
                schema: "quality");
        }
    }
}
