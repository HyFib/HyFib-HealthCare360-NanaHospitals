using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreControlsDebitNotesRecall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "received_quantity",
                schema: "supplychain",
                table: "stock_transfer_lines",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_recalled",
                schema: "supplychain",
                table: "stock_batches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "store_item_levels",
                schema: "supplychain",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reorder_level = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    reorder_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    minimum_stock = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    maximum_stock = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
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
                    table.PrimaryKey("pk_store_item_levels", x => x.id);
                    table.ForeignKey(
                        name: "fk_store_item_levels_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "supplychain",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_store_item_levels_stores_store_id",
                        column: x => x.store_id,
                        principalSchema: "supplychain",
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vendor_debit_notes",
                schema: "supplychain",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goods_receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vendor_reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_inter_state = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    raised_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    posted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    taxable_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
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
                    table.PrimaryKey("pk_vendor_debit_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_vendor_debit_notes_stores_store_id",
                        column: x => x.store_id,
                        principalSchema: "supplychain",
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vendor_debit_notes_vendors_vendor_id",
                        column: x => x.vendor_id,
                        principalSchema: "supplychain",
                        principalTable: "vendors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vendor_payable_adjustments",
                schema: "supplychain",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    vendor_reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    raised_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    posted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_vendor_payable_adjustments", x => x.id);
                    table.ForeignKey(
                        name: "fk_vendor_payable_adjustments_vendors_vendor_id",
                        column: x => x.vendor_id,
                        principalSchema: "supplychain",
                        principalTable: "vendors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vendor_debit_note_lines",
                schema: "supplychain",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vendor_debit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    batch_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    gst_rate = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    taxable_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vendor_debit_note_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_vendor_debit_note_lines_vendor_debit_notes_vendor_debit_not",
                        column: x => x.vendor_debit_note_id,
                        principalSchema: "supplychain",
                        principalTable: "vendor_debit_notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_store_item_levels_facility_id",
                schema: "supplychain",
                table: "store_item_levels",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_store_item_levels_is_deleted",
                schema: "supplychain",
                table: "store_item_levels",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_store_item_levels_item_id",
                schema: "supplychain",
                table: "store_item_levels",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_store_item_levels_store_id_item_id",
                schema: "supplychain",
                table: "store_item_levels",
                columns: new[] { "store_id", "item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vendor_debit_note_lines_stock_batch_id",
                schema: "supplychain",
                table: "vendor_debit_note_lines",
                column: "stock_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendor_debit_note_lines_vendor_debit_note_id",
                schema: "supplychain",
                table: "vendor_debit_note_lines",
                column: "vendor_debit_note_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendor_debit_notes_facility_id",
                schema: "supplychain",
                table: "vendor_debit_notes",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendor_debit_notes_facility_id_number",
                schema: "supplychain",
                table: "vendor_debit_notes",
                columns: new[] { "facility_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vendor_debit_notes_facility_id_status_posted_at",
                schema: "supplychain",
                table: "vendor_debit_notes",
                columns: new[] { "facility_id", "status", "posted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vendor_debit_notes_is_deleted",
                schema: "supplychain",
                table: "vendor_debit_notes",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_vendor_debit_notes_store_id",
                schema: "supplychain",
                table: "vendor_debit_notes",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendor_debit_notes_vendor_id",
                schema: "supplychain",
                table: "vendor_debit_notes",
                column: "vendor_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendor_payable_adjustments_facility_id",
                schema: "supplychain",
                table: "vendor_payable_adjustments",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendor_payable_adjustments_facility_id_number",
                schema: "supplychain",
                table: "vendor_payable_adjustments",
                columns: new[] { "facility_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vendor_payable_adjustments_facility_id_status_posted_at",
                schema: "supplychain",
                table: "vendor_payable_adjustments",
                columns: new[] { "facility_id", "status", "posted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vendor_payable_adjustments_is_deleted",
                schema: "supplychain",
                table: "vendor_payable_adjustments",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_vendor_payable_adjustments_vendor_id",
                schema: "supplychain",
                table: "vendor_payable_adjustments",
                column: "vendor_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "store_item_levels",
                schema: "supplychain");

            migrationBuilder.DropTable(
                name: "vendor_debit_note_lines",
                schema: "supplychain");

            migrationBuilder.DropTable(
                name: "vendor_payable_adjustments",
                schema: "supplychain");

            migrationBuilder.DropTable(
                name: "vendor_debit_notes",
                schema: "supplychain");

            migrationBuilder.DropColumn(
                name: "received_quantity",
                schema: "supplychain",
                table: "stock_transfer_lines");

            migrationBuilder.DropColumn(
                name: "is_recalled",
                schema: "supplychain",
                table: "stock_batches");
        }
    }
}
