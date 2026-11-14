using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "restore_test_note",
                schema: "platform",
                table: "backup_runs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "restore_test_passed",
                schema: "platform",
                table: "backup_runs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "restore_tested_at",
                schema: "platform",
                table: "backup_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "secondary_copies",
                schema: "platform",
                table: "backup_runs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "restore_test_note",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "restore_test_passed",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "restore_tested_at",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "secondary_copies",
                schema: "platform",
                table: "backup_runs");
        }
    }
}
