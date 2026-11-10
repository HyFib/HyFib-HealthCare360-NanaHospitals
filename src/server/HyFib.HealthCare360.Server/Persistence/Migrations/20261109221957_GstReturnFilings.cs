using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GstReturnFilings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "entered_from_paper_at",
                schema: "billing",
                table: "bills",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gst_return_filings",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gstin = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    return_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    arn = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    filed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    corrected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    corrected_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    correction_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_gst_return_filings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gst_return_filings_gstin_return_type_year_month",
                schema: "billing",
                table: "gst_return_filings",
                columns: new[] { "gstin", "return_type", "year", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gst_return_filings_is_deleted",
                schema: "billing",
                table: "gst_return_filings",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gst_return_filings",
                schema: "billing");

            migrationBuilder.DropColumn(
                name: "entered_from_paper_at",
                schema: "billing",
                table: "bills");
        }
    }
}
