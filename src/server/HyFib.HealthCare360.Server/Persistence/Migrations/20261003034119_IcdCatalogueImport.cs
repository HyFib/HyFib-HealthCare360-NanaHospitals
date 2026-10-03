using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IcdCatalogueImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "withdrawn_at",
                schema: "clinical",
                table: "procedure_codes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "search_text",
                schema: "clinical",
                table: "icd_codes",
                type: "character varying(2700)",
                maxLength: 2700,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(700)",
                oldMaxLength: 700);

            migrationBuilder.AddColumn<string>(
                name: "included_terms",
                schema: "clinical",
                table: "icd_codes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage",
                schema: "clinical",
                table: "icd_codes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "withdrawn_at",
                schema: "clinical",
                table: "icd_codes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "catalogue_imports",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalogue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    release = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    source_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    codes_in_file = table.Column<int>(type: "integer", nullable: false),
                    added = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    unchanged = table.Column<int>(type: "integer", nullable: false),
                    reinstated = table.Column<int>(type: "integer", nullable: false),
                    withdrawn = table.Column<int>(type: "integer", nullable: false),
                    left_alone = table.Column<int>(type: "integer", nullable: false),
                    chapters = table.Column<int>(type: "integer", nullable: false),
                    blocks = table.Column<int>(type: "integer", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    channel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_catalogue_imports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "icd_classification_entries",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    release = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    start = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    end = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    chapter_number = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    parent_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    holds_categories = table.Column<bool>(type: "boolean", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_icd_classification_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_catalogue_imports_catalogue_imported_at",
                schema: "clinical",
                table: "catalogue_imports",
                columns: new[] { "catalogue", "imported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_catalogue_imports_is_deleted",
                schema: "clinical",
                table: "catalogue_imports",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_icd_classification_entries_release_kind_code",
                schema: "clinical",
                table: "icd_classification_entries",
                columns: new[] { "release", "kind", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The earlier build knows no "Import" source and keeps search text to 700 characters: imported codes become
            // starter-list codes for it, and their search text (inclusion terms) is cut to fit.
            migrationBuilder.Sql("UPDATE clinical.icd_codes SET source = 'Seed' WHERE source = 'Import';");
            migrationBuilder.Sql("UPDATE clinical.procedure_codes SET source = 'Seed' WHERE source = 'Import';");
            migrationBuilder.Sql(
                "UPDATE clinical.icd_codes SET search_text = left(search_text, 700) WHERE length(search_text) > 700;");

            migrationBuilder.DropTable(
                name: "catalogue_imports",
                schema: "clinical");

            migrationBuilder.DropTable(
                name: "icd_classification_entries",
                schema: "clinical");

            migrationBuilder.DropColumn(
                name: "withdrawn_at",
                schema: "clinical",
                table: "procedure_codes");

            migrationBuilder.DropColumn(
                name: "included_terms",
                schema: "clinical",
                table: "icd_codes");

            migrationBuilder.DropColumn(
                name: "usage",
                schema: "clinical",
                table: "icd_codes");

            migrationBuilder.DropColumn(
                name: "withdrawn_at",
                schema: "clinical",
                table: "icd_codes");

            migrationBuilder.AlterColumn<string>(
                name: "search_text",
                schema: "clinical",
                table: "icd_codes",
                type: "character varying(700)",
                maxLength: 700,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2700)",
                oldMaxLength: 2700);
        }
    }
}
