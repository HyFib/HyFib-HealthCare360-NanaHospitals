using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HbpMasterImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hbp_master_imports",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    master_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rows = table.Column<string>(type: "text", nullable: false),
                    rows_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    master_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    read = table.Column<int>(type: "integer", nullable: false),
                    added = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    retired = table.Column<int>(type: "integer", nullable: false),
                    problem_count = table.Column<int>(type: "integer", nullable: false),
                    problems = table.Column<List<string>>(type: "text[]", nullable: false),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asked_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_hbp_master_imports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hbp_master_imports_approval_id",
                schema: "billing",
                table: "hbp_master_imports",
                column: "approval_id");

            migrationBuilder.CreateIndex(
                name: "ix_hbp_master_imports_asked_at",
                schema: "billing",
                table: "hbp_master_imports",
                column: "asked_at");

            migrationBuilder.CreateIndex(
                name: "ix_hbp_master_imports_is_deleted",
                schema: "billing",
                table: "hbp_master_imports",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hbp_master_imports",
                schema: "billing");
        }
    }
}
