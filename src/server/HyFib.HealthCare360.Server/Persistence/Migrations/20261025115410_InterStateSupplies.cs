using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InterStateSupplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "buyer_gstin",
                schema: "pharmacy",
                table: "sales",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "buyer_legal_name",
                schema: "pharmacy",
                table: "sales",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "igst_amount",
                schema: "pharmacy",
                table: "sales",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "is_inter_state",
                schema: "pharmacy",
                table: "sales",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "place_of_supply",
                schema: "pharmacy",
                table: "sales",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "igst_amount",
                schema: "pharmacy",
                table: "sale_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "igst_amount",
                schema: "billing",
                table: "bills",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "is_inter_state",
                schema: "billing",
                table: "bills",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "place_of_supply",
                schema: "billing",
                table: "bills",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "igst_amount",
                schema: "billing",
                table: "bill_lines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "buyer_gstin",
                schema: "pharmacy",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "buyer_legal_name",
                schema: "pharmacy",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "igst_amount",
                schema: "pharmacy",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "is_inter_state",
                schema: "pharmacy",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "place_of_supply",
                schema: "pharmacy",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "igst_amount",
                schema: "pharmacy",
                table: "sale_items");

            migrationBuilder.DropColumn(
                name: "igst_amount",
                schema: "billing",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "is_inter_state",
                schema: "billing",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "place_of_supply",
                schema: "billing",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "igst_amount",
                schema: "billing",
                table: "bill_lines");
        }
    }
}
