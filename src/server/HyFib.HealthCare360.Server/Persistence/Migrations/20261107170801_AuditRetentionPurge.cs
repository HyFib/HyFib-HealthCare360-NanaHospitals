using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditRetentionPurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_purges",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retention_years = table.Column<int>(type: "integer", nullable: false),
                    cutoff_month = table.Column<DateOnly>(type: "date", nullable: false),
                    months = table.Column<string>(type: "text", nullable: false),
                    row_count = table.Column<long>(type: "bigint", nullable: false),
                    first_chain_index = table.Column<long>(type: "bigint", nullable: false),
                    boundary_chain_index = table.Column<long>(type: "bigint", nullable: false),
                    boundary_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    export_file = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    export_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    mac = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_purges", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_purges_boundary_chain_index",
                schema: "audit",
                table: "audit_purges",
                column: "boundary_chain_index",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_purges",
                schema: "audit");
        }
    }
}
