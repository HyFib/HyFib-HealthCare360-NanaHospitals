using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoleSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Written by hand: EF Core scaffolds false, which would turn every existing role off on upgrade. Every role is
            // in use until an administrator turns it off (RoleSwitchMigrationTests).
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "identity",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "identity",
                table: "roles");
        }
    }
}
