using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SurgicalConsentGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "anaesthesia_consent_id",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "anaesthesia_consent_required_at_sign_in",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "consent_waived_at",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "consent_waived_by_name",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "consent_waived_by_user_id",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "consent_waiver_concurred_by_name",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "consent_waiver_concurred_by_user_id",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "consent_waiver_reason",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "procedure_consent_id",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "anaesthesia_consent_id",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "anaesthesia_consent_required_at_sign_in",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "consent_waived_at",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "consent_waived_by_name",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "consent_waived_by_user_id",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "consent_waiver_concurred_by_name",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "consent_waiver_concurred_by_user_id",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "consent_waiver_reason",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "procedure_consent_id",
                schema: "specialtycare",
                table: "surgery_bookings");
        }
    }
}
