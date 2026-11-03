using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreditNoteGst : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "preceding_id",
                schema: "billing",
                table: "einvoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preceding_source",
                schema: "billing",
                table: "einvoices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "cgst_amount",
                schema: "billing",
                table: "credit_notes",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "gst_time_barred",
                schema: "billing",
                table: "credit_notes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "igst_amount",
                schema: "billing",
                table: "credit_notes",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "sgst_amount",
                schema: "billing",
                table: "credit_notes",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "credit_note_lines",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sac_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    gst_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    taxable_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_note_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_credit_note_lines_bill_lines_bill_line_id",
                        column: x => x.bill_line_id,
                        principalSchema: "billing",
                        principalTable: "bill_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_credit_note_lines_credit_notes_credit_note_id",
                        column: x => x.credit_note_id,
                        principalSchema: "billing",
                        principalTable: "credit_notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_einvoices_preceding_source_preceding_id",
                schema: "billing",
                table: "einvoices",
                columns: new[] { "preceding_source", "preceding_id" },
                filter: "preceding_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_lines_bill_line_id",
                schema: "billing",
                table: "credit_note_lines",
                column: "bill_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_lines_credit_note_id",
                schema: "billing",
                table: "credit_note_lines",
                column: "credit_note_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credit_note_lines",
                schema: "billing");

            migrationBuilder.DropIndex(
                name: "ix_einvoices_preceding_source_preceding_id",
                schema: "billing",
                table: "einvoices");

            migrationBuilder.DropColumn(
                name: "preceding_id",
                schema: "billing",
                table: "einvoices");

            migrationBuilder.DropColumn(
                name: "preceding_source",
                schema: "billing",
                table: "einvoices");

            migrationBuilder.DropColumn(
                name: "cgst_amount",
                schema: "billing",
                table: "credit_notes");

            migrationBuilder.DropColumn(
                name: "gst_time_barred",
                schema: "billing",
                table: "credit_notes");

            migrationBuilder.DropColumn(
                name: "igst_amount",
                schema: "billing",
                table: "credit_notes");

            migrationBuilder.DropColumn(
                name: "sgst_amount",
                schema: "billing",
                table: "credit_notes");
        }
    }
}
