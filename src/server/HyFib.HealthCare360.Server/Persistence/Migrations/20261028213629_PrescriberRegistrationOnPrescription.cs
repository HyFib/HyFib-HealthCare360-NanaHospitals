using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrescriberRegistrationOnPrescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "prescriber_registration_council",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prescriber_registration_number",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "prescriber_registration_council",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "prescriber_registration_number",
                schema: "clinical",
                table: "prescriptions");
        }
    }
}
