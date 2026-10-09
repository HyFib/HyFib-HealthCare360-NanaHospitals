using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignInHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_failed_login_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "temporary_password_expires_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // Temporary passwords given out before they had an end work for 72 hours from the upgrade. The first
            // administrator's one-time password, and passwords to be changed because they were found in a breach, do not
            // expire.
            migrationBuilder.Sql("""
                UPDATE identity.users SET temporary_password_expires_at = now() + interval '72 hours'
                WHERE type = 'Staff' AND must_change_password AND password_breached_at IS NULL AND normalized_user_name <> 'admin';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_failed_login_at",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "temporary_password_expires_at",
                schema: "identity",
                table: "users");
        }
    }
}
