using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GatewaySettlementCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_settlement_corrections",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway_settlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    on = table.Column<DateOnly>(type: "date", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gateway_settlement_corrections", x => x.id);
                    table.ForeignKey(
                        name: "fk_gateway_settlement_corrections_gateway_settlements_gateway_",
                        column: x => x.gateway_settlement_id,
                        principalSchema: "billing",
                        principalTable: "gateway_settlements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gateway_settlement_corrections_gateway_settlement_id_seq",
                schema: "billing",
                table: "gateway_settlement_corrections",
                columns: new[] { "gateway_settlement_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gateway_settlement_corrections_on",
                schema: "billing",
                table: "gateway_settlement_corrections",
                column: "on");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_settlement_corrections",
                schema: "billing");
        }
    }
}
