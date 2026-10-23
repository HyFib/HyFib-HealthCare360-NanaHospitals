using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CorporateCheckupPayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "corporate_payer_id",
                schema: "clinical",
                table: "package_bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "payer_id",
                schema: "clinical",
                table: "corporate_checkup_batches",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "corporate_payer_id",
                schema: "clinical",
                table: "package_bookings");

            migrationBuilder.DropColumn(
                name: "payer_id",
                schema: "clinical",
                table: "corporate_checkup_batches");
        }
    }
}
