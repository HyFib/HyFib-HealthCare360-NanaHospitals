using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PosSettlementWithdrawals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pos_settlement_lines_facility_id_line_key",
                schema: "billing",
                table: "pos_settlement_lines");

            migrationBuilder.DropIndex(
                name: "ix_pos_settlement_imports_facility_id_file_sha256",
                schema: "billing",
                table: "pos_settlement_imports");

            migrationBuilder.AddColumn<DateOnly>(
                name: "withdrawn_on",
                schema: "billing",
                table: "pos_settlement_lines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "withdraw_reason",
                schema: "billing",
                table: "pos_settlement_imports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "withdrawn_at",
                schema: "billing",
                table: "pos_settlement_imports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "withdrawn_by_name",
                schema: "billing",
                table: "pos_settlement_imports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "withdrawn_on",
                schema: "billing",
                table: "pos_settlement_imports",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_facility_id_line_key",
                schema: "billing",
                table: "pos_settlement_lines",
                columns: new[] { "facility_id", "line_key" },
                unique: true,
                filter: "withdrawn_on IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_facility_id_withdrawn_on",
                schema: "billing",
                table: "pos_settlement_lines",
                columns: new[] { "facility_id", "withdrawn_on" },
                filter: "withdrawn_on IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_imports_facility_id_file_sha256",
                schema: "billing",
                table: "pos_settlement_imports",
                columns: new[] { "facility_id", "file_sha256" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pos_settlement_lines_facility_id_line_key",
                schema: "billing",
                table: "pos_settlement_lines");

            migrationBuilder.DropIndex(
                name: "ix_pos_settlement_lines_facility_id_withdrawn_on",
                schema: "billing",
                table: "pos_settlement_lines");

            migrationBuilder.DropIndex(
                name: "ix_pos_settlement_imports_facility_id_file_sha256",
                schema: "billing",
                table: "pos_settlement_imports");

            migrationBuilder.DropColumn(
                name: "withdrawn_on",
                schema: "billing",
                table: "pos_settlement_lines");

            migrationBuilder.DropColumn(
                name: "withdraw_reason",
                schema: "billing",
                table: "pos_settlement_imports");

            migrationBuilder.DropColumn(
                name: "withdrawn_at",
                schema: "billing",
                table: "pos_settlement_imports");

            migrationBuilder.DropColumn(
                name: "withdrawn_by_name",
                schema: "billing",
                table: "pos_settlement_imports");

            migrationBuilder.DropColumn(
                name: "withdrawn_on",
                schema: "billing",
                table: "pos_settlement_imports");

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_facility_id_line_key",
                schema: "billing",
                table: "pos_settlement_lines",
                columns: new[] { "facility_id", "line_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_imports_facility_id_file_sha256",
                schema: "billing",
                table: "pos_settlement_imports",
                columns: new[] { "facility_id", "file_sha256" },
                unique: true);
        }
    }
}
