using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceTrustedCaFingerprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trusted_ca_fingerprints",
                schema: "identity",
                table: "devices",
                type: "character varying(259)",
                maxLength: 259,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trusted_ca_fingerprints",
                schema: "identity",
                table: "devices");
        }
    }
}
