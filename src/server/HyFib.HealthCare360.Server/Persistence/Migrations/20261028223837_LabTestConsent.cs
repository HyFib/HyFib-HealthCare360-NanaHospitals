using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabTestConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "consent_basis",
                schema: "diagnostics",
                table: "lab_order_items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "consent_given_by",
                schema: "diagnostics",
                table: "lab_order_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "consent_recorded_at",
                schema: "diagnostics",
                table: "lab_order_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "consent_recorded_by",
                schema: "diagnostics",
                table: "lab_order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "consent_reference",
                schema: "diagnostics",
                table: "lab_order_items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "consent_relation",
                schema: "diagnostics",
                table: "lab_order_items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consent_basis",
                schema: "diagnostics",
                table: "lab_order_items");

            migrationBuilder.DropColumn(
                name: "consent_given_by",
                schema: "diagnostics",
                table: "lab_order_items");

            migrationBuilder.DropColumn(
                name: "consent_recorded_at",
                schema: "diagnostics",
                table: "lab_order_items");

            migrationBuilder.DropColumn(
                name: "consent_recorded_by",
                schema: "diagnostics",
                table: "lab_order_items");

            migrationBuilder.DropColumn(
                name: "consent_reference",
                schema: "diagnostics",
                table: "lab_order_items");

            migrationBuilder.DropColumn(
                name: "consent_relation",
                schema: "diagnostics",
                table: "lab_order_items");
        }
    }
}
