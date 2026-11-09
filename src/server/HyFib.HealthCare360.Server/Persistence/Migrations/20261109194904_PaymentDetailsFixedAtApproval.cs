using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentDetailsFixedAtApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bank_account_cipher",
                schema: "hr",
                table: "payslips",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bank_ifsc_cipher",
                schema: "hr",
                table: "payslips",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bank_details_fixed_at",
                schema: "hr",
                table: "payroll_runs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bank_account_cipher",
                schema: "hr",
                table: "payslips");

            migrationBuilder.DropColumn(
                name: "bank_ifsc_cipher",
                schema: "hr",
                table: "payslips");

            migrationBuilder.DropColumn(
                name: "bank_details_fixed_at",
                schema: "hr",
                table: "payroll_runs");
        }
    }
}
