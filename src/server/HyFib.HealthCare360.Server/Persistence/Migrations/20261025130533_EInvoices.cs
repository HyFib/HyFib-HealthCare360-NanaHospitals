using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "buyer_address",
                schema: "pharmacy",
                table: "sales",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "einvoices",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    document_date = table.Column<DateOnly>(type: "date", nullable: false),
                    buyer_gstin = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    invoice_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    irn = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ack_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ack_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    signed_qr_code = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    recorded_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    cancel_remark = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
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
                    table.PrimaryKey("pk_einvoices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_einvoices_facility_id",
                schema: "billing",
                table: "einvoices",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_einvoices_facility_id_document_date",
                schema: "billing",
                table: "einvoices",
                columns: new[] { "facility_id", "document_date" });

            migrationBuilder.CreateIndex(
                name: "ix_einvoices_irn",
                schema: "billing",
                table: "einvoices",
                column: "irn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_einvoices_is_deleted",
                schema: "billing",
                table: "einvoices",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_einvoices_source_source_id",
                schema: "billing",
                table: "einvoices",
                columns: new[] { "source", "source_id" },
                unique: true,
                filter: "status = 'Registered' AND is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "einvoices",
                schema: "billing");

            migrationBuilder.DropColumn(
                name: "buyer_address",
                schema: "pharmacy",
                table: "sales");
        }
    }
}
