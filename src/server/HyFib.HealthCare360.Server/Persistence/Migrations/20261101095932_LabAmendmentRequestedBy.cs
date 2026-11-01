using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabAmendmentRequestedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "requested_by",
                schema: "diagnostics",
                table: "lab_report_amendments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_by_name",
                schema: "diagnostics",
                table: "lab_report_amendments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "requested_by",
                schema: "diagnostics",
                table: "lab_report_amendments");

            migrationBuilder.DropColumn(
                name: "requested_by_name",
                schema: "diagnostics",
                table: "lab_report_amendments");
        }
    }
}
