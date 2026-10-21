using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabGatewayNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_gateway_nodes",
                schema: "integrations",
                columns: table => new
                {
                    node = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    statuses = table.Column<string>(type: "jsonb", nullable: false),
                    relaying_agents = table.Column<string>(type: "jsonb", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lab_gateway_nodes", x => x.node);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_gateway_nodes",
                schema: "integrations");
        }
    }
}
