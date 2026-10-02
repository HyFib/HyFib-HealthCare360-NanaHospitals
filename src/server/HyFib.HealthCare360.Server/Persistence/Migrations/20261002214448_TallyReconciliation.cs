using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TallyReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "confirmed_count",
                schema: "billing",
                table: "tally_exports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "file_downloaded_at",
                schema: "billing",
                table: "tally_exports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "file_fingerprint",
                schema: "billing",
                table: "tally_exports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "file_voucher_count",
                schema: "billing",
                table: "tally_exports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "imported_at",
                schema: "billing",
                table: "tally_exports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "imported_by_name",
                schema: "billing",
                table: "tally_exports",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by_user_id",
                schema: "billing",
                table: "tally_exports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "queued_at",
                schema: "billing",
                table: "tally_exports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "records_check",
                schema: "billing",
                table: "tally_exports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "records_checked_at",
                schema: "billing",
                table: "tally_exports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "records_differences",
                schema: "billing",
                table: "tally_exports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "rejected_count",
                schema: "billing",
                table: "tally_exports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                schema: "billing",
                table: "tally_exports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tally_check",
                schema: "billing",
                table: "tally_exports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<string>(
                name: "tally_check_message",
                schema: "billing",
                table: "tally_exports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "tally_checked_at",
                schema: "billing",
                table: "tally_exports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tally_differences",
                schema: "billing",
                table: "tally_exports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "simulator_tally_vouchers",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    remote_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    voucher_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    xml = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    alterations = table.Column<int>(type: "integer", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_simulator_tally_vouchers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tally_vouchers",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    export_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    remote_id = table.Column<string>(type: "character varying(210)", maxLength: 210, nullable: true),
                    voucher_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    in_day_book = table.Column<bool>(type: "boolean", nullable: false),
                    answer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    answer_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    confirmed_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    match = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tally_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    tally_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    tally_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tally_vouchers", x => x.id);
                    table.ForeignKey(
                        name: "fk_tally_vouchers_tally_exports_export_id",
                        column: x => x.export_id,
                        principalSchema: "billing",
                        principalTable: "tally_exports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_simulator_tally_vouchers_company_date",
                schema: "integrations",
                table: "simulator_tally_vouchers",
                columns: new[] { "company", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_simulator_tally_vouchers_company_remote_id",
                schema: "integrations",
                table: "simulator_tally_vouchers",
                columns: new[] { "company", "remote_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tally_vouchers_date_key",
                schema: "billing",
                table: "tally_vouchers",
                columns: new[] { "date", "key" });

            migrationBuilder.CreateIndex(
                name: "ix_tally_vouchers_export_id_key",
                schema: "billing",
                table: "tally_vouchers",
                columns: new[] { "export_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tally_vouchers_facility_id_date",
                schema: "billing",
                table: "tally_vouchers",
                columns: new[] { "facility_id", "date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "simulator_tally_vouchers",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "tally_vouchers",
                schema: "billing");

            migrationBuilder.DropColumn(
                name: "confirmed_count",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "file_downloaded_at",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "file_fingerprint",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "file_voucher_count",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "imported_at",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "imported_by_name",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "imported_by_user_id",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "queued_at",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "records_check",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "records_checked_at",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "records_differences",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "rejected_count",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "run_id",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "tally_check",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "tally_check_message",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "tally_checked_at",
                schema: "billing",
                table: "tally_exports");

            migrationBuilder.DropColumn(
                name: "tally_differences",
                schema: "billing",
                table: "tally_exports");
        }
    }
}
