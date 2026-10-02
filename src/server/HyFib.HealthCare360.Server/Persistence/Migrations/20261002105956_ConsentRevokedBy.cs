using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsentRevokedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "revoked_by_user_id",
                schema: "patients",
                table: "patient_consents",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "revoked_by_user_id",
                schema: "patients",
                table: "patient_consents");
        }
    }
}
