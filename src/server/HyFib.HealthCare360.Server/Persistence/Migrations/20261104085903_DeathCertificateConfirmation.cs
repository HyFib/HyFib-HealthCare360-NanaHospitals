using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeathCertificateConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "confirmed_at",
                schema: "clinical",
                table: "death_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "confirmed_by",
                schema: "clinical",
                table: "death_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "confirmed_by_user_id",
                schema: "clinical",
                table: "death_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "confirmer_registration_number",
                schema: "clinical",
                table: "death_records",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "confirmed_at",
                schema: "clinical",
                table: "death_records");

            migrationBuilder.DropColumn(
                name: "confirmed_by",
                schema: "clinical",
                table: "death_records");

            migrationBuilder.DropColumn(
                name: "confirmed_by_user_id",
                schema: "clinical",
                table: "death_records");

            migrationBuilder.DropColumn(
                name: "confirmer_registration_number",
                schema: "clinical",
                table: "death_records");
        }
    }
}
