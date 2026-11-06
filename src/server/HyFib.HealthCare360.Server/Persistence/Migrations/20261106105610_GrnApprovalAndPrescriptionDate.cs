using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrnApprovalAndPrescriptionDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "prescription_date",
                schema: "pharmacy",
                table: "sales",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "vendor_invoice_amount",
                schema: "supplychain",
                table: "goods_receipts",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "prescription_date",
                schema: "pharmacy",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "vendor_invoice_amount",
                schema: "supplychain",
                table: "goods_receipts");
        }
    }
}
