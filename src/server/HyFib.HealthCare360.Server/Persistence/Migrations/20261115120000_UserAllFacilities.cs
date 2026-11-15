using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserAllFacilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "all_facilities",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // An empty list of facilities meant every facility, and now means none: a member of staff (or a service account)
            // who had none chosen keeps every facility, now given explicitly. A patient's account never works in a facility.
            migrationBuilder.Sql("""
                UPDATE identity.users AS u
                SET all_facilities = TRUE
                WHERE u.type <> 'Patient'
                  AND NOT EXISTS (SELECT 1 FROM identity.user_facilities AS f WHERE f.user_id = u.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to an empty list meaning every facility: a user left with no facility since then has every facility
            // again. The way back from an upgrade is the backup taken just before it (DEPLOYMENT.md, "Going back").
            migrationBuilder.DropColumn(
                name: "all_facilities",
                schema: "identity",
                table: "users");
        }
    }
}
