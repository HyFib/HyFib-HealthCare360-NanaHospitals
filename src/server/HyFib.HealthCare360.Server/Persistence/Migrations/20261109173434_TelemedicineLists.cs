using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelemedicineLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "telemedicine_list",
                schema: "supplychain",
                table: "items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // The law's list only (Telemedicine Practice Guidelines 2020): Schedule X and NDPS medicines are on the Prohibited
            // List. Lists O, A and B are the pharmacy and therapeutics committee's to fill in, so nothing else is set.
            migrationBuilder.Sql(
                "UPDATE supplychain.items SET telemedicine_list = 'Prohibited' WHERE type = 'Drug' AND (is_controlled OR schedule = 'X');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "telemedicine_list",
                schema: "supplychain",
                table: "items");
        }
    }
}
