using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MedicationOrderCountersignature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "refilled_by_name",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "countersign_due_at",
                schema: "inpatient",
                table: "medication_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "countersign_overdue_notified_at",
                schema: "inpatient",
                table: "medication_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "countersigned_at",
                schema: "inpatient",
                table: "medication_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "countersigned_by",
                schema: "inpatient",
                table: "medication_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "countersigned_by_name",
                schema: "inpatient",
                table: "medication_orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "entered_by",
                schema: "inpatient",
                table: "medication_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "entered_by_name",
                schema: "inpatient",
                table: "medication_orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_emergency",
                schema: "inpatient",
                table: "medication_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Every order charted before this was signed by its prescriber, or came from a signed e-prescription or the pharmacy.
            migrationBuilder.AddColumn<string>(
                name: "mode",
                schema: "inpatient",
                table: "medication_orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Written");

            migrationBuilder.AddColumn<bool>(
                name: "read_back",
                schema: "inpatient",
                table: "medication_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "stop_countersign_due_at",
                schema: "inpatient",
                table: "medication_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "stop_overdue_notified_at",
                schema: "inpatient",
                table: "medication_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stop_request_reason",
                schema: "inpatient",
                table: "medication_orders",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "stop_requested_at",
                schema: "inpatient",
                table: "medication_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "stop_requested_by",
                schema: "inpatient",
                table: "medication_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stop_requested_by_name",
                schema: "inpatient",
                table: "medication_orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_medication_orders_countersign_due_at",
                schema: "inpatient",
                table: "medication_orders",
                column: "countersign_due_at",
                filter: "countersigned_at IS NULL AND mode <> 'Written'");

            migrationBuilder.CreateIndex(
                name: "ix_medication_orders_stop_countersign_due_at",
                schema: "inpatient",
                table: "medication_orders",
                column: "stop_countersign_due_at",
                filter: "stop_requested_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_medication_orders_countersign_due_at",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropIndex(
                name: "ix_medication_orders_stop_countersign_due_at",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "refilled_by_name",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "countersign_due_at",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "countersign_overdue_notified_at",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "countersigned_at",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "countersigned_by",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "countersigned_by_name",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "entered_by",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "entered_by_name",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "is_emergency",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "mode",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "read_back",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "stop_countersign_due_at",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "stop_overdue_notified_at",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "stop_request_reason",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "stop_requested_at",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "stop_requested_by",
                schema: "inpatient",
                table: "medication_orders");

            migrationBuilder.DropColumn(
                name: "stop_requested_by_name",
                schema: "inpatient",
                table: "medication_orders");
        }
    }
}
