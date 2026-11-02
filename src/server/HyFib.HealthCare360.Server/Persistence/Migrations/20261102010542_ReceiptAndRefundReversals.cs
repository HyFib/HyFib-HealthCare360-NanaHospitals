using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptAndRefundReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_doctor_fee_accruals_bill_line_id",
                schema: "billing",
                table: "doctor_fee_accruals");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "payout_reversed_at",
                schema: "billing",
                table: "refunds",
                type: "timestamp with time zone",
                nullable: true);

            // Accruals made before this migration are each their bill line's first: occurrence 1.
            migrationBuilder.AddColumn<int>(
                name: "occurrence",
                schema: "billing",
                table: "doctor_fee_accruals",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "receipt_reversals",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    advance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    caused_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("pk_receipt_reversals", x => x.id);
                    table.ForeignKey(
                        name: "fk_receipt_reversals_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipt_reversals_receipt_reversals_caused_by_id",
                        column: x => x.caused_by_id,
                        principalSchema: "billing",
                        principalTable: "receipt_reversals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipt_reversals_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalSchema: "billing",
                        principalTable: "receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refund_reversals",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    advance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    mode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    gateway_refund_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("pk_refund_reversals", x => x.id);
                    table.ForeignKey(
                        name: "fk_refund_reversals_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_reversals_refunds_refund_id",
                        column: x => x.refund_id,
                        principalSchema: "billing",
                        principalTable: "refunds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt_reversal_lines",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reversal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_reversal_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_receipt_reversal_lines_receipt_payments_receipt_payment_id",
                        column: x => x.receipt_payment_id,
                        principalSchema: "billing",
                        principalTable: "receipt_payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipt_reversal_lines_receipt_reversals_reversal_id",
                        column: x => x.reversal_id,
                        principalSchema: "billing",
                        principalTable: "receipt_reversals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_doctor_fee_accruals_bill_line_id_occurrence",
                schema: "billing",
                table: "doctor_fee_accruals",
                columns: new[] { "bill_line_id", "occurrence" },
                unique: true,
                filter: "reversal_of_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversal_lines_receipt_payment_id",
                schema: "billing",
                table: "receipt_reversal_lines",
                column: "receipt_payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversal_lines_reversal_id",
                schema: "billing",
                table: "receipt_reversal_lines",
                column: "reversal_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_advance_id",
                schema: "billing",
                table: "receipt_reversals",
                column: "advance_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_bill_id",
                schema: "billing",
                table: "receipt_reversals",
                column: "bill_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_caused_by_id",
                schema: "billing",
                table: "receipt_reversals",
                column: "caused_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_facility_id",
                schema: "billing",
                table: "receipt_reversals",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_facility_id_reversed_at",
                schema: "billing",
                table: "receipt_reversals",
                columns: new[] { "facility_id", "reversed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_ip_account_id",
                schema: "billing",
                table: "receipt_reversals",
                column: "ip_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_is_deleted",
                schema: "billing",
                table: "receipt_reversals",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_number",
                schema: "billing",
                table: "receipt_reversals",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_patient_id",
                schema: "billing",
                table: "receipt_reversals",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_receipt_id",
                schema: "billing",
                table: "receipt_reversals",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_reversals_advance_id",
                schema: "billing",
                table: "refund_reversals",
                column: "advance_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_reversals_bill_id",
                schema: "billing",
                table: "refund_reversals",
                column: "bill_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_reversals_facility_id",
                schema: "billing",
                table: "refund_reversals",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_reversals_facility_id_reversed_at",
                schema: "billing",
                table: "refund_reversals",
                columns: new[] { "facility_id", "reversed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refund_reversals_ip_account_id",
                schema: "billing",
                table: "refund_reversals",
                column: "ip_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_reversals_is_deleted",
                schema: "billing",
                table: "refund_reversals",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_refund_reversals_number",
                schema: "billing",
                table: "refund_reversals",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_reversals_patient_id",
                schema: "billing",
                table: "refund_reversals",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_reversals_refund_id",
                schema: "billing",
                table: "refund_reversals",
                column: "refund_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "receipt_reversal_lines",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "refund_reversals",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "receipt_reversals",
                schema: "billing");

            migrationBuilder.DropIndex(
                name: "ix_doctor_fee_accruals_bill_line_id_occurrence",
                schema: "billing",
                table: "doctor_fee_accruals");

            migrationBuilder.DropColumn(
                name: "payout_reversed_at",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "occurrence",
                schema: "billing",
                table: "doctor_fee_accruals");

            migrationBuilder.CreateIndex(
                name: "ix_doctor_fee_accruals_bill_line_id",
                schema: "billing",
                table: "doctor_fee_accruals",
                column: "bill_line_id",
                unique: true,
                filter: "reversal_of_id IS NULL");
        }
    }
}
