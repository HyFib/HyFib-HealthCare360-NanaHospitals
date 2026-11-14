using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupOffsiteAndKeyProofs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "offsite_attempts",
                schema: "platform",
                table: "backup_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "offsite_error",
                schema: "platform",
                table: "backup_runs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offsite_key",
                schema: "platform",
                table: "backup_runs",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "offsite_size_bytes",
                schema: "platform",
                table: "backup_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offsite_upload",
                schema: "platform",
                table: "backup_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "offsite_uploaded_at",
                schema: "platform",
                table: "backup_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "backup_key_proofs",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    proven_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_key_proofs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_key_proofs_key_id_proven_at",
                schema: "platform",
                table: "backup_key_proofs",
                columns: new[] { "key_id", "proven_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_key_proofs",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "offsite_attempts",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "offsite_error",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "offsite_key",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "offsite_size_bytes",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "offsite_upload",
                schema: "platform",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "offsite_uploaded_at",
                schema: "platform",
                table: "backup_runs");
        }
    }
}
