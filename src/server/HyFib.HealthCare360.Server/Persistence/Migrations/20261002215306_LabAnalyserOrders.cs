using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabAnalyserOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_order_downloads",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sample_barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel_codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    source_message_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lab_order_downloads", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lab_order_downloads_created_at",
                schema: "integrations",
                table: "lab_order_downloads",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_lab_order_downloads_facility_id",
                schema: "integrations",
                table: "lab_order_downloads",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_lab_order_downloads_instrument_code_sample_barcode",
                schema: "integrations",
                table: "lab_order_downloads",
                columns: new[] { "instrument_code", "sample_barcode" });

            migrationBuilder.CreateIndex(
                name: "ix_lab_order_downloads_instrument_code_status_next_attempt_at",
                schema: "integrations",
                table: "lab_order_downloads",
                columns: new[] { "instrument_code", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_lab_order_downloads_source_message_id_instrument_code",
                schema: "integrations",
                table: "lab_order_downloads",
                columns: new[] { "source_message_id", "instrument_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_order_downloads",
                schema: "integrations");
        }
    }
}
