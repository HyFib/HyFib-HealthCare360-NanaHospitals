using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NikshayNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "nikshay_id",
                schema: "clinical",
                table: "notifiable_cases",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "nikshay_notified_on",
                schema: "clinical",
                table: "notifiable_cases",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "nikshay_recorded_at",
                schema: "clinical",
                table: "notifiable_cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "nikshay_recorded_by",
                schema: "clinical",
                table: "notifiable_cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifiable_cases_disease_code_status_nikshay_notified_on",
                schema: "clinical",
                table: "notifiable_cases",
                columns: new[] { "disease_code", "status", "nikshay_notified_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifiable_cases_disease_code_status_nikshay_notified_on",
                schema: "clinical",
                table: "notifiable_cases");

            migrationBuilder.DropColumn(
                name: "nikshay_id",
                schema: "clinical",
                table: "notifiable_cases");

            migrationBuilder.DropColumn(
                name: "nikshay_notified_on",
                schema: "clinical",
                table: "notifiable_cases");

            migrationBuilder.DropColumn(
                name: "nikshay_recorded_at",
                schema: "clinical",
                table: "notifiable_cases");

            migrationBuilder.DropColumn(
                name: "nikshay_recorded_by",
                schema: "clinical",
                table: "notifiable_cases");
        }
    }
}
