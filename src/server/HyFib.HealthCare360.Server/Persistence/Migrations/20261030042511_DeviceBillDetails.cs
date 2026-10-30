using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <summary>
    /// A medical device's bill details (NPPA): the brand, maker, batch, specification and MRP on charges and bill lines, the
    /// specification and NPPA ceiling price on the item master, and on an OT implant or consumable; and the price an IP
    /// pharmacy line was charged at, which a return is credited at.
    /// </summary>
    public partial class DeviceBillDetails : Migration
    {
        /// <summary>Lines issued before the upgrade were charged at their MRP, so that is their price: a return credits it.</summary>
        public const string PriceIssuedLinesAtTheirMrp = "UPDATE pharmacy.ip_issue_lines SET unit_price = unit_mrp;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "brand",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ceiling_price",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "mrp",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "specification",
                schema: "specialtycare",
                table: "surgery_materials",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ceiling_price",
                schema: "supplychain",
                table: "items",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "specification",
                schema: "supplychain",
                table: "items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_price",
                schema: "pharmacy",
                table: "ip_issue_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(PriceIssuedLinesAtTheirMrp);

            migrationBuilder.AddColumn<string>(
                name: "device",
                schema: "billing",
                table: "charges",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "device",
                schema: "billing",
                table: "bill_lines",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "brand",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "ceiling_price",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "mrp",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "specification",
                schema: "specialtycare",
                table: "surgery_materials");

            migrationBuilder.DropColumn(
                name: "ceiling_price",
                schema: "supplychain",
                table: "items");

            migrationBuilder.DropColumn(
                name: "specification",
                schema: "supplychain",
                table: "items");

            migrationBuilder.DropColumn(
                name: "unit_price",
                schema: "pharmacy",
                table: "ip_issue_lines");

            migrationBuilder.DropColumn(
                name: "device",
                schema: "billing",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "device",
                schema: "billing",
                table: "bill_lines");
        }
    }
}
