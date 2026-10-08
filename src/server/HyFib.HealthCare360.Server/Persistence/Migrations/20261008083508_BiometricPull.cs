using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BiometricPull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "terminal_id",
                schema: "integrations",
                table: "simulator_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "clock_offset_seconds",
                schema: "hr",
                table: "biometric_devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "connection",
                schema: "hr",
                table: "biometric_devices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Push");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_pull_at",
                schema: "hr",
                table: "biometric_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_pull_error",
                schema: "hr",
                table: "biometric_devices",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "offline_since",
                schema: "hr",
                table: "biometric_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pull_address",
                schema: "hr",
                table: "biometric_devices",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pull_every_minutes",
                schema: "hr",
                table: "biometric_devices",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "pull_port",
                schema: "hr",
                table: "biometric_devices",
                type: "integer",
                nullable: false,
                defaultValue: 4370);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pulled_up_to",
                schema: "hr",
                table: "biometric_devices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "clock_offset_seconds",
                schema: "hr",
                table: "biometric_devices");

            migrationBuilder.DropColumn(
                name: "connection",
                schema: "hr",
                table: "biometric_devices");

            migrationBuilder.DropColumn(
                name: "last_pull_at",
                schema: "hr",
                table: "biometric_devices");

            migrationBuilder.DropColumn(
                name: "last_pull_error",
                schema: "hr",
                table: "biometric_devices");

            migrationBuilder.DropColumn(
                name: "offline_since",
                schema: "hr",
                table: "biometric_devices");

            migrationBuilder.DropColumn(
                name: "pull_address",
                schema: "hr",
                table: "biometric_devices");

            migrationBuilder.DropColumn(
                name: "pull_every_minutes",
                schema: "hr",
                table: "biometric_devices");

            migrationBuilder.DropColumn(
                name: "pull_port",
                schema: "hr",
                table: "biometric_devices");

            migrationBuilder.DropColumn(
                name: "pulled_up_to",
                schema: "hr",
                table: "biometric_devices");

            migrationBuilder.AlterColumn<string>(
                name: "terminal_id",
                schema: "integrations",
                table: "simulator_transactions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}
