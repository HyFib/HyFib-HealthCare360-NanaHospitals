using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TheatreSecondPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correction_approved_by_name",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correction_reason",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correction_requested_by_name",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "corrects_material_id",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "superseded_at",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "superseded_by_material_id",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "checklist_voids",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "count_discrepancies",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "counts",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "site_level",
                schema: "specialtycare",
                table: "surgery_bookings",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "correction_approved_by_name",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "correction_reason",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "correction_requested_by_name",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "corrects_material_id",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "superseded_at",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "superseded_by_material_id",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "checklist_voids",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "count_discrepancies",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "counts",
                schema: "specialtycare",
                table: "surgery_bookings");

            migrationBuilder.DropColumn(
                name: "site_level",
                schema: "specialtycare",
                table: "surgery_bookings");
        }
    }
}
