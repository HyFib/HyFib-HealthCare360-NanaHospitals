using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GstReturnsIrpDebitNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ewb_date",
                schema: "billing",
                table: "einvoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ewb_no",
                schema: "billing",
                table: "einvoices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ewb_valid_till",
                schema: "billing",
                table: "einvoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider",
                schema: "billing",
                table: "einvoices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // IRNs from before were all recorded by hand from the portal.
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "signed_invoice",
                schema: "billing",
                table: "einvoices",
                type: "character varying(100000)",
                maxLength: 100000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "debit_amount",
                schema: "billing",
                table: "bills",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "payer_debit_amount",
                schema: "billing",
                table: "bills",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "debit_notes",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    taxable_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debit_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_debit_notes_bills_bill_id",
                        column: x => x.bill_id,
                        principalSchema: "billing",
                        principalTable: "bills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "debit_note_lines",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    debit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sac_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    gst_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    taxable_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debit_note_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_debit_note_lines_bill_lines_bill_line_id",
                        column: x => x.bill_line_id,
                        principalSchema: "billing",
                        principalTable: "bill_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debit_note_lines_debit_notes_debit_note_id",
                        column: x => x.debit_note_id,
                        principalSchema: "billing",
                        principalTable: "debit_notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_debit_note_lines_bill_line_id",
                schema: "billing",
                table: "debit_note_lines",
                column: "bill_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_debit_note_lines_debit_note_id",
                schema: "billing",
                table: "debit_note_lines",
                column: "debit_note_id");

            migrationBuilder.CreateIndex(
                name: "ix_debit_notes_bill_id",
                schema: "billing",
                table: "debit_notes",
                column: "bill_id");

            migrationBuilder.CreateIndex(
                name: "ix_debit_notes_facility_id",
                schema: "billing",
                table: "debit_notes",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_debit_notes_facility_id_issued_at",
                schema: "billing",
                table: "debit_notes",
                columns: new[] { "facility_id", "issued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_debit_notes_is_deleted",
                schema: "billing",
                table: "debit_notes",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_debit_notes_number",
                schema: "billing",
                table: "debit_notes",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "debit_note_lines",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "debit_notes",
                schema: "billing");

            migrationBuilder.DropColumn(
                name: "ewb_date",
                schema: "billing",
                table: "einvoices");

            migrationBuilder.DropColumn(
                name: "ewb_no",
                schema: "billing",
                table: "einvoices");

            migrationBuilder.DropColumn(
                name: "ewb_valid_till",
                schema: "billing",
                table: "einvoices");

            migrationBuilder.DropColumn(
                name: "provider",
                schema: "billing",
                table: "einvoices");

            migrationBuilder.DropColumn(
                name: "signed_invoice",
                schema: "billing",
                table: "einvoices");

            migrationBuilder.DropColumn(
                name: "debit_amount",
                schema: "billing",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "payer_debit_amount",
                schema: "billing",
                table: "bills");
        }
    }
}
