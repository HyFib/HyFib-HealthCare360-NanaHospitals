using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CounterTerminalPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "purpose",
                schema: "billing",
                table: "pos_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<Guid>(
                name: "patient_id",
                schema: "billing",
                table: "pos_transactions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "owner",
                schema: "billing",
                table: "pos_transactions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owner_document",
                schema: "billing",
                table: "pos_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_payment_id",
                schema: "billing",
                table: "pos_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approval_code",
                schema: "pharmacy",
                table: "sale_payments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "card_last4",
                schema: "pharmacy",
                table: "sale_payments",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "terminal_sale_id",
                schema: "pharmacy",
                table: "sale_payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approval_code",
                schema: "supportservices",
                table: "canteen_sales",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "card_last4",
                schema: "supportservices",
                table: "canteen_sales",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "terminal_sale_id",
                schema: "supportservices",
                table: "canteen_sales",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_pos_transactions_owner_payment_id",
                schema: "billing",
                table: "pos_transactions",
                column: "owner_payment_id",
                unique: true,
                filter: "owner_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sale_payments_terminal_sale_id",
                schema: "pharmacy",
                table: "sale_payments",
                column: "terminal_sale_id",
                unique: true,
                filter: "terminal_sale_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_canteen_sales_terminal_sale_id",
                schema: "supportservices",
                table: "canteen_sales",
                column: "terminal_sale_id",
                unique: true,
                filter: "terminal_sale_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sale_payments_terminal_sale_id",
                schema: "pharmacy",
                table: "sale_payments");

            migrationBuilder.DropIndex(
                name: "ix_pos_transactions_owner_payment_id",
                schema: "billing",
                table: "pos_transactions");

            migrationBuilder.DropIndex(
                name: "ix_canteen_sales_terminal_sale_id",
                schema: "supportservices",
                table: "canteen_sales");

            migrationBuilder.DropColumn(
                name: "approval_code",
                schema: "pharmacy",
                table: "sale_payments");

            migrationBuilder.DropColumn(
                name: "card_last4",
                schema: "pharmacy",
                table: "sale_payments");

            migrationBuilder.DropColumn(
                name: "terminal_sale_id",
                schema: "pharmacy",
                table: "sale_payments");

            migrationBuilder.DropColumn(
                name: "owner",
                schema: "billing",
                table: "pos_transactions");

            migrationBuilder.DropColumn(
                name: "owner_document",
                schema: "billing",
                table: "pos_transactions");

            migrationBuilder.DropColumn(
                name: "owner_payment_id",
                schema: "billing",
                table: "pos_transactions");

            migrationBuilder.DropColumn(
                name: "approval_code",
                schema: "supportservices",
                table: "canteen_sales");

            migrationBuilder.DropColumn(
                name: "card_last4",
                schema: "supportservices",
                table: "canteen_sales");

            migrationBuilder.DropColumn(
                name: "terminal_sale_id",
                schema: "supportservices",
                table: "canteen_sales");

            migrationBuilder.AlterColumn<string>(
                name: "purpose",
                schema: "billing",
                table: "pos_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "patient_id",
                schema: "billing",
                table: "pos_transactions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
