using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentDisputes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "dispute_id",
                schema: "billing",
                table: "pos_settlement_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payment_disputes",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payment_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    gateway_settlement_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pos_settlement_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    return_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    respond_by = table.Column<DateOnly>(type: "date", nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opened_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    evidence_sent_on = table.Column<DateOnly>(type: "date", nullable: true),
                    evidence_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    evidence_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    loss_asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    loss_asked_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    loss_subject_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    loss_subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reversal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reversal_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
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
                    table.PrimaryKey("pk_payment_disputes", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_disputes_receipt_payment_receipt_payment_id",
                        column: x => x.receipt_payment_id,
                        principalSchema: "billing",
                        principalTable: "receipt_payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_disputes_receipt_receipt_id",
                        column: x => x.receipt_id,
                        principalSchema: "billing",
                        principalTable: "receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_dispute_reminders",
                schema: "billing",
                columns: table => new
                {
                    dispute_id = table.Column<Guid>(type: "uuid", nullable: false),
                    announced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reminded_on = table.Column<DateOnly>(type: "date", nullable: true),
                    reminded_for = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_dispute_reminders", x => x.dispute_id);
                    table.ForeignKey(
                        name: "fk_payment_dispute_reminders_payment_disputes_dispute_id",
                        column: x => x.dispute_id,
                        principalSchema: "billing",
                        principalTable: "payment_disputes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_dispute_reminders_announced_at",
                schema: "billing",
                table: "payment_dispute_reminders",
                column: "announced_at",
                filter: "announced_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_facility_id",
                schema: "billing",
                table: "payment_disputes",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_facility_id_status",
                schema: "billing",
                table: "payment_disputes",
                columns: new[] { "facility_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_gateway_settlement_line_id",
                schema: "billing",
                table: "payment_disputes",
                column: "gateway_settlement_line_id",
                unique: true,
                filter: "gateway_settlement_line_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_is_deleted",
                schema: "billing",
                table: "payment_disputes",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_number",
                schema: "billing",
                table: "payment_disputes",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_pos_settlement_line_id",
                schema: "billing",
                table: "payment_disputes",
                column: "pos_settlement_line_id",
                unique: true,
                filter: "pos_settlement_line_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_receipt_id",
                schema: "billing",
                table: "payment_disputes",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_receipt_payment_id",
                schema: "billing",
                table: "payment_disputes",
                column: "receipt_payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_disputes_return_line_id",
                schema: "billing",
                table: "payment_disputes",
                column: "return_line_id",
                unique: true,
                filter: "return_line_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_dispute_reminders",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "payment_disputes",
                schema: "billing");

            migrationBuilder.DropColumn(
                name: "dispute_id",
                schema: "billing",
                table: "pos_settlement_lines");
        }
    }
}
