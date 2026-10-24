using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaperCharts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_paper_inpatient",
                schema: "clinical",
                table: "medical_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "paper_episode_from",
                schema: "clinical",
                table: "medical_records",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "paper_episode_to",
                schema: "clinical",
                table: "medical_records",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "paper_file_number",
                schema: "clinical",
                table: "medical_records",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "paper_note",
                schema: "clinical",
                table: "medical_records",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_medical_records_facility_id_paper_file_number",
                schema: "clinical",
                table: "medical_records",
                columns: new[] { "facility_id", "paper_file_number" },
                unique: true,
                filter: "paper_file_number IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_medical_records_facility_id_paper_file_number",
                schema: "clinical",
                table: "medical_records");

            migrationBuilder.DropColumn(
                name: "is_paper_inpatient",
                schema: "clinical",
                table: "medical_records");

            migrationBuilder.DropColumn(
                name: "paper_episode_from",
                schema: "clinical",
                table: "medical_records");

            migrationBuilder.DropColumn(
                name: "paper_episode_to",
                schema: "clinical",
                table: "medical_records");

            migrationBuilder.DropColumn(
                name: "paper_file_number",
                schema: "clinical",
                table: "medical_records");

            migrationBuilder.DropColumn(
                name: "paper_note",
                schema: "clinical",
                table: "medical_records");
        }
    }
}
