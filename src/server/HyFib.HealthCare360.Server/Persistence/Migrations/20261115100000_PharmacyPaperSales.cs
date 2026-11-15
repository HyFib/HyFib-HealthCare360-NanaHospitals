using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PharmacyPaperSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A pharmacy sale written on paper during a downtime: when it was entered (its sold-at is the time on the paper).
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "entered_from_paper_at",
                schema: "pharmacy",
                table: "sales",
                type: "timestamp with time zone",
                nullable: true);

            // A prescription dispensed on paper during a downtime, closed in the queue so it is not dispensed again.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dispensed_on_paper_at",
                schema: "pharmacy",
                table: "queue_entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "paper_sale_number",
                schema: "pharmacy",
                table: "queue_entries",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "entered_from_paper_at",
                schema: "pharmacy",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "dispensed_on_paper_at",
                schema: "pharmacy",
                table: "queue_entries");

            migrationBuilder.DropColumn(
                name: "paper_sale_number",
                schema: "pharmacy",
                table: "queue_entries");
        }
    }
}
