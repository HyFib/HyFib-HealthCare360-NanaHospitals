using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_active_at",
                schema: "identity",
                table: "user_sessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "locked_at",
                schema: "identity",
                table: "user_sessions",
                type: "timestamp with time zone",
                nullable: true);

            // A session open at the upgrade counts its last refresh as its last use, so the upgrade signs nobody out.
            migrationBuilder.Sql("UPDATE identity.user_sessions SET last_active_at = last_seen_at;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_active_at",
                schema: "identity",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "locked_at",
                schema: "identity",
                table: "user_sessions");
        }
    }
}
