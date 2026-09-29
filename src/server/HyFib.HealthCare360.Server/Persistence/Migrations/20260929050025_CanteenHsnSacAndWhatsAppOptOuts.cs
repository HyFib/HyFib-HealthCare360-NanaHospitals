using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CanteenHsnSacAndWhatsAppOptOuts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "keyword_action",
                schema: "integrations",
                table: "whatsapp_inbound_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hsn_sac",
                schema: "supportservices",
                table: "menu_items",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hsn_sac",
                schema: "supportservices",
                table: "canteen_sale_lines",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            // Installs seeded before items had codes: an item without one is billed as canteen food (SAC 996333), so the
            // seeded packaged goods get their HSN codes. Bills already issued keep what they printed.
            migrationBuilder.Sql("""
                UPDATE supportservices.menu_items
                SET hsn_sac = CASE code WHEN 'WATER-1L' THEN '220190' WHEN 'BISCUITS' THEN '190531' WHEN 'TENDER-COCONUT' THEN '080119' END
                WHERE hsn_sac IS NULL AND code IN ('WATER-1L', 'BISCUITS', 'TENDER-COCONUT');
                """);

            migrationBuilder.CreateTable(
                name: "whatsapp_opt_outs",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opted_out = table.Column<bool>(type: "boolean", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_whatsapp_opt_outs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_opt_outs_is_deleted",
                schema: "platform",
                table: "whatsapp_opt_outs",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_opt_outs_phone",
                schema: "platform",
                table: "whatsapp_opt_outs",
                column: "phone",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whatsapp_opt_outs",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "keyword_action",
                schema: "integrations",
                table: "whatsapp_inbound_messages");

            migrationBuilder.DropColumn(
                name: "hsn_sac",
                schema: "supportservices",
                table: "menu_items");

            migrationBuilder.DropColumn(
                name: "hsn_sac",
                schema: "supportservices",
                table: "canteen_sale_lines");
        }
    }
}
