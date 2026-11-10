using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_incidents",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    server = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    told_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reminded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    second_contact_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    support_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    on_call_message_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    second_contact_message_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    support_message_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
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
                    table.PrimaryKey("pk_alert_incidents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_incidents_code_server",
                schema: "platform",
                table: "alert_incidents",
                columns: new[] { "code", "server" },
                unique: true,
                filter: "cleared_at IS NULL AND NOT is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_alert_incidents_is_deleted",
                schema: "platform",
                table: "alert_incidents",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_alert_incidents_raised_at",
                schema: "platform",
                table: "alert_incidents",
                column: "raised_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_incidents",
                schema: "platform");
        }
    }
}
