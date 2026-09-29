using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "document_count",
                schema: "platform",
                table: "backup_runs",
                type: "integer",
                nullable: true);

            // Backups recorded before this migration hold the database only: format 1.
            migrationBuilder.AddColumn<int>(
                name: "format_version",
                schema: "platform",
                table: "backup_runs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_at",
                schema: "platform",
                table: "backup_runs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "document_count",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "format_version",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "verified_at",
                schema: "platform",
                table: "backup_runs");
        }
    }
}
