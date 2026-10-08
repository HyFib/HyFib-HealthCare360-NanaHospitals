using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PosSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pos_settlement_imports",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acquirer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    rows = table.Column<int>(type: "integer", nullable: false),
                    added = table.Column<int>(type: "integer", nullable: false),
                    already_imported = table.Column<int>(type: "integer", nullable: false),
                    ignored = table.Column<int>(type: "integer", nullable: false),
                    first_settled_on = table.Column<DateOnly>(type: "date", nullable: true),
                    last_settled_on = table.Column<DateOnly>(type: "date", nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pos_settlement_imports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pos_settlement_lines",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    acquirer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    mode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    terminal_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    transaction_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    transaction_on = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    charges = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    net = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    rrn = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    approval_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    card_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    settled_on = table.Column<DateOnly>(type: "date", nullable: false),
                    utr = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    match = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    match_basis = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receipt_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    matched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    review_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pos_settlement_lines", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_receipt_payments_reference",
                schema: "billing",
                table: "receipt_payments",
                column: "reference",
                filter: "reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pos_transactions_approval_code",
                schema: "billing",
                table: "pos_transactions",
                column: "approval_code",
                filter: "approval_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_imports_facility_id",
                schema: "billing",
                table: "pos_settlement_imports",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_imports_facility_id_file_sha256",
                schema: "billing",
                table: "pos_settlement_imports",
                columns: new[] { "facility_id", "file_sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_imports_facility_id_imported_at",
                schema: "billing",
                table: "pos_settlement_imports",
                columns: new[] { "facility_id", "imported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_facility_id",
                schema: "billing",
                table: "pos_settlement_lines",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_facility_id_line_key",
                schema: "billing",
                table: "pos_settlement_lines",
                columns: new[] { "facility_id", "line_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_facility_id_match_reviewed_at",
                schema: "billing",
                table: "pos_settlement_lines",
                columns: new[] { "facility_id", "match", "reviewed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_facility_id_settled_on",
                schema: "billing",
                table: "pos_settlement_lines",
                columns: new[] { "facility_id", "settled_on" });

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_import_id",
                schema: "billing",
                table: "pos_settlement_lines",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_receipt_payment_id",
                schema: "billing",
                table: "pos_settlement_lines",
                column: "receipt_payment_id",
                unique: true,
                filter: "receipt_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pos_settlement_lines_refund_id",
                schema: "billing",
                table: "pos_settlement_lines",
                column: "refund_id",
                unique: true,
                filter: "refund_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pos_settlement_imports",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "pos_settlement_lines",
                schema: "billing");

            migrationBuilder.DropIndex(
                name: "ix_receipt_payments_reference",
                schema: "billing",
                table: "receipt_payments");

            migrationBuilder.DropIndex(
                name: "ix_pos_transactions_approval_code",
                schema: "billing",
                table: "pos_transactions");
        }
    }
}
