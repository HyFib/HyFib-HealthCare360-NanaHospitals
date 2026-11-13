using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WifiSampleNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "node",
                schema: "platform",
                table: "wifi_samples",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_wifi_samples_node_at",
                schema: "platform",
                table: "wifi_samples",
                columns: new[] { "node", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_wifi_samples_node_at",
                schema: "platform",
                table: "wifi_samples");

            migrationBuilder.DropColumn(
                name: "node",
                schema: "platform",
                table: "wifi_samples");
        }
    }
}
