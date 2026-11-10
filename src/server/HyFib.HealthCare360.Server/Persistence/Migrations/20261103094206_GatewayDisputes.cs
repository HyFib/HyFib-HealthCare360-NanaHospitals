using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GatewayDisputes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "gateway_dispute_id",
                schema: "billing",
                table: "payment_disputes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_phase",
                schema: "billing",
                table: "payment_disputes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_status",
                schema: "billing",
                table: "payment_disputes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lost_told_at",
                schema: "billing",
                table: "payment_dispute_reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dispute_id",
                schema: "billing",
                table: "gateway_webhook_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dispute_id",
                schema: "billing",
                table: "gateway_settlement_lines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_gateway_dispute_id",
                schema: "billing",
                table: "payment_disputes",
                column: "gateway_dispute_id",
                unique: true,
                filter: "gateway_dispute_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_disputes_gateway_dispute_id",
                schema: "billing",
                table: "payment_disputes");

            migrationBuilder.DropColumn(
                name: "gateway_dispute_id",
                schema: "billing",
                table: "payment_disputes");

            migrationBuilder.DropColumn(
                name: "gateway_phase",
                schema: "billing",
                table: "payment_disputes");

            migrationBuilder.DropColumn(
                name: "gateway_status",
                schema: "billing",
                table: "payment_disputes");

            migrationBuilder.DropColumn(
                name: "lost_told_at",
                schema: "billing",
                table: "payment_dispute_reminders");

            migrationBuilder.DropColumn(
                name: "dispute_id",
                schema: "billing",
                table: "gateway_webhook_events");

            migrationBuilder.DropColumn(
                name: "dispute_id",
                schema: "billing",
                table: "gateway_settlement_lines");
        }
    }
}
