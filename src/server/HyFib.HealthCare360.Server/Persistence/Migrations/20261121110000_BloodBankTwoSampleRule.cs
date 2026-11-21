using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BloodBankTwoSampleRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "disagreeing_sample_at",
                schema: "specialtycare",
                table: "blood_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "disagreeing_sample_drawn_at",
                schema: "specialtycare",
                table: "blood_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disagreeing_sample_drawn_by",
                schema: "specialtycare",
                table: "blood_requests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disagreeing_sample_group",
                schema: "specialtycare",
                table: "blood_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disagreeing_sample_request_number",
                schema: "specialtycare",
                table: "blood_requests",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_sample_drawn_at",
                schema: "specialtycare",
                table: "blood_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_sample_drawn_by",
                schema: "specialtycare",
                table: "blood_requests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sample_drawn_at",
                schema: "specialtycare",
                table: "blood_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sample_drawn_by",
                schema: "specialtycare",
                table: "blood_requests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "second_sample_required",
                schema: "specialtycare",
                table: "blood_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "single_sample_override_at",
                schema: "specialtycare",
                table: "blood_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "single_sample_override_by",
                schema: "specialtycare",
                table: "blood_requests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "single_sample_override_by_practitioner_id",
                schema: "specialtycare",
                table: "blood_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "single_sample_override_by_user_id",
                schema: "specialtycare",
                table: "blood_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "single_sample_override_reason",
                schema: "specialtycare",
                table: "blood_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "disagreeing_sample_at",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "disagreeing_sample_drawn_at",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "disagreeing_sample_drawn_by",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "disagreeing_sample_group",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "disagreeing_sample_request_number",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "first_sample_drawn_at",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "first_sample_drawn_by",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "sample_drawn_at",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "sample_drawn_by",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "second_sample_required",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "single_sample_override_at",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "single_sample_override_by",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "single_sample_override_by_practitioner_id",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "single_sample_override_by_user_id",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "single_sample_override_reason",
                schema: "specialtycare",
                table: "blood_requests");
        }
    }
}
