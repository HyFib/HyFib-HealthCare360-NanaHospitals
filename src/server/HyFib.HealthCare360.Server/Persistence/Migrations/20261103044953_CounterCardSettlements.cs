using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CounterCardSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "counter",
                schema: "billing",
                table: "pos_settlement_lines",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "counter_document",
                schema: "billing",
                table: "pos_settlement_lines",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "counter_payment_id",
                schema: "billing",
                table: "pos_settlement_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_counter_payment_id",
                schema: "billing",
                table: "pos_settlement_lines",
                column: "counter_payment_id",
                unique: true,
                filter: "counter_payment_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pos_settlement_lines_counter_payment_id",
                schema: "billing",
                table: "pos_settlement_lines");

            migrationBuilder.DropColumn(
                name: "counter",
                schema: "billing",
                table: "pos_settlement_lines");

            migrationBuilder.DropColumn(
                name: "counter_document",
                schema: "billing",
                table: "pos_settlement_lines");

            migrationBuilder.DropColumn(
                name: "counter_payment_id",
                schema: "billing",
                table: "pos_settlement_lines");
        }
    }
}
