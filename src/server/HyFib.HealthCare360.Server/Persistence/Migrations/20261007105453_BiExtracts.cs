using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BiExtracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every schedule saved before BI extracts is an e-mail.
            migrationBuilder.AddColumn<string>(
                name: "destination",
                schema: "analytics",
                table: "report_schedules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Email");

            migrationBuilder.AddColumn<string>(
                name: "extract_name",
                schema: "analytics",
                table: "report_schedules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_succeeded_at",
                schema: "analytics",
                table: "report_schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_report_schedules_facility_id_extract_name",
                schema: "analytics",
                table: "report_schedules",
                columns: new[] { "facility_id", "extract_name" },
                unique: true,
                filter: "extract_name IS NOT NULL AND is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_report_schedules_facility_id_extract_name",
                schema: "analytics",
                table: "report_schedules");

            migrationBuilder.DropColumn(
                name: "destination",
                schema: "analytics",
                table: "report_schedules");

            migrationBuilder.DropColumn(
                name: "extract_name",
                schema: "analytics",
                table: "report_schedules");

            migrationBuilder.DropColumn(
                name: "last_succeeded_at",
                schema: "analytics",
                table: "report_schedules");
        }
    }
}
