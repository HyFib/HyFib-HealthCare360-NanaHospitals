using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CashierShiftShortage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "shortage_accepted_at",
                schema: "billing",
                table: "cashier_shifts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "shortage_accepted_by",
                schema: "billing",
                table: "cashier_shifts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shortage_accepted_by_name",
                schema: "billing",
                table: "cashier_shifts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "shortage_awaiting",
                schema: "billing",
                table: "cashier_shifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "shortage_accepted_at",
                schema: "billing",
                table: "cashier_shifts");

            migrationBuilder.DropColumn(
                name: "shortage_accepted_by",
                schema: "billing",
                table: "cashier_shifts");

            migrationBuilder.DropColumn(
                name: "shortage_accepted_by_name",
                schema: "billing",
                table: "cashier_shifts");

            migrationBuilder.DropColumn(
                name: "shortage_awaiting",
                schema: "billing",
                table: "cashier_shifts");
        }
    }
}
