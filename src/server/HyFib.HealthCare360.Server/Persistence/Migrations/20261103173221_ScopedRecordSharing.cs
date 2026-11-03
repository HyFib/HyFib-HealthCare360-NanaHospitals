using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopedRecordSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "share_kinds",
                schema: "patients",
                table: "patient_consents",
                type: "character varying(32)[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "share_purposes",
                schema: "patients",
                table: "patient_consents",
                type: "character varying(32)[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<DateOnly>(
                name: "share_records_from",
                schema: "patients",
                table: "patient_consents",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "share_records_to",
                schema: "patients",
                table: "patient_consents",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "share_until",
                schema: "patients",
                table: "patient_consents",
                type: "date",
                nullable: true);

            // Existing consents name no accounts: an empty array, as the two above get, leaves them open as they were.
            migrationBuilder.AddColumn<List<Guid>>(
                name: "share_with",
                schema: "patients",
                table: "patient_consents",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "share_kinds",
                schema: "patients",
                table: "patient_consents");

            migrationBuilder.DropColumn(
                name: "share_purposes",
                schema: "patients",
                table: "patient_consents");

            migrationBuilder.DropColumn(
                name: "share_records_from",
                schema: "patients",
                table: "patient_consents");

            migrationBuilder.DropColumn(
                name: "share_records_to",
                schema: "patients",
                table: "patient_consents");

            migrationBuilder.DropColumn(
                name: "share_until",
                schema: "patients",
                table: "patient_consents");

            migrationBuilder.DropColumn(
                name: "share_with",
                schema: "patients",
                table: "patient_consents");
        }
    }
}
