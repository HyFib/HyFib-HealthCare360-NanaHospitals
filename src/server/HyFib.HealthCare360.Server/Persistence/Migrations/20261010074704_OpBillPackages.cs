using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpBillPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Out-patient bills could not use packages before, so every package made so far prices admissions.
            migrationBuilder.AddColumn<string>(
                name: "used_for",
                schema: "billing",
                table: "packages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "InPatient");

            migrationBuilder.AddColumn<Guid>(
                name: "package_id",
                schema: "billing",
                table: "bills",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_bills_package_id",
                schema: "billing",
                table: "bills",
                column: "package_id");

            migrationBuilder.AddForeignKey(
                name: "fk_bills_package_package_id",
                schema: "billing",
                table: "bills",
                column: "package_id",
                principalSchema: "billing",
                principalTable: "packages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_bills_package_package_id",
                schema: "billing",
                table: "bills");

            migrationBuilder.DropIndex(
                name: "ix_bills_package_id",
                schema: "billing",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "used_for",
                schema: "billing",
                table: "packages");

            migrationBuilder.DropColumn(
                name: "package_id",
                schema: "billing",
                table: "bills");
        }
    }
}
