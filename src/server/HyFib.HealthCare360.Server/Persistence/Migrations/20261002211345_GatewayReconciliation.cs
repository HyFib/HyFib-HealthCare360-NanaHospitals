using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GatewayReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "settled_at",
                schema: "integrations",
                table: "simulator_transactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settlement_id",
                schema: "integrations",
                table: "simulator_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_error",
                schema: "billing",
                table: "refunds",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_reference",
                schema: "billing",
                table: "refunds",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "gateway_settled_at",
                schema: "billing",
                table: "refunds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_status",
                schema: "billing",
                table: "refunds",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gateway_discrepancies",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    gateway_order_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settlement_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    gateway_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    gateway_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resolution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    resolved_automatically = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_gateway_discrepancies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gateway_settlements",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    settlement_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    utr = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    payments_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    refunds_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    adjustments_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    fees = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    settlement_fees = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    settlement_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_on = table.Column<DateOnly>(type: "date", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gateway_settlements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gateway_webhook_events",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    refund_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    settlement_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    deliveries = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gateway_webhook_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gateway_settlement_lines",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway_settlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    fee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    credit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    debit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    transaction_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    match = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gateway_settlement_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_gateway_settlement_lines_gateway_settlements_gateway_settle",
                        column: x => x.gateway_settlement_id,
                        principalSchema: "billing",
                        principalTable: "gateway_settlements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_simulator_transactions_settled_at",
                schema: "integrations",
                table: "simulator_transactions",
                column: "settled_at");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_gateway_refund_id",
                schema: "billing",
                table: "refunds",
                column: "gateway_refund_id",
                unique: true,
                filter: "gateway_refund_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_gateway_order_id",
                schema: "scheduling",
                table: "appointments",
                column: "gateway_order_id",
                filter: "gateway_order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_discrepancies_facility_id",
                schema: "billing",
                table: "gateway_discrepancies",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_discrepancies_facility_id_status_rank",
                schema: "billing",
                table: "gateway_discrepancies",
                columns: new[] { "facility_id", "status", "rank" });

            migrationBuilder.CreateIndex(
                name: "ix_gateway_discrepancies_gateway_order_record_id",
                schema: "billing",
                table: "gateway_discrepancies",
                column: "gateway_order_record_id");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_discrepancies_is_deleted",
                schema: "billing",
                table: "gateway_discrepancies",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_discrepancies_kind_key",
                schema: "billing",
                table: "gateway_discrepancies",
                columns: new[] { "kind", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gateway_discrepancies_refund_id",
                schema: "billing",
                table: "gateway_discrepancies",
                column: "refund_id");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_settlement_lines_entity_id",
                schema: "billing",
                table: "gateway_settlement_lines",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_settlement_lines_facility_id_settled_at",
                schema: "billing",
                table: "gateway_settlement_lines",
                columns: new[] { "facility_id", "settled_at" });

            migrationBuilder.CreateIndex(
                name: "ix_gateway_settlement_lines_gateway_settlement_id",
                schema: "billing",
                table: "gateway_settlement_lines",
                column: "gateway_settlement_id");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_settlements_facility_id",
                schema: "billing",
                table: "gateway_settlements",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_settlements_facility_id_settled_on",
                schema: "billing",
                table: "gateway_settlements",
                columns: new[] { "facility_id", "settled_on" });

            migrationBuilder.CreateIndex(
                name: "ix_gateway_settlements_provider_settlement_id",
                schema: "billing",
                table: "gateway_settlements",
                columns: new[] { "provider", "settlement_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gateway_settlements_settled_on",
                schema: "billing",
                table: "gateway_settlements",
                column: "settled_on");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_webhook_events_provider_event_id",
                schema: "billing",
                table: "gateway_webhook_events",
                columns: new[] { "provider", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gateway_webhook_events_received_at",
                schema: "billing",
                table: "gateway_webhook_events",
                column: "received_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_discrepancies",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "gateway_settlement_lines",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "gateway_webhook_events",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "gateway_settlements",
                schema: "billing");

            migrationBuilder.DropIndex(
                name: "ix_simulator_transactions_settled_at",
                schema: "integrations",
                table: "simulator_transactions");

            migrationBuilder.DropIndex(
                name: "ix_refunds_gateway_refund_id",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropIndex(
                name: "ix_appointments_gateway_order_id",
                schema: "scheduling",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "settled_at",
                schema: "integrations",
                table: "simulator_transactions");

            migrationBuilder.DropColumn(
                name: "settlement_id",
                schema: "integrations",
                table: "simulator_transactions");

            migrationBuilder.DropColumn(
                name: "gateway_error",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "gateway_reference",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "gateway_settled_at",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "gateway_status",
                schema: "billing",
                table: "refunds");
        }
    }
}
