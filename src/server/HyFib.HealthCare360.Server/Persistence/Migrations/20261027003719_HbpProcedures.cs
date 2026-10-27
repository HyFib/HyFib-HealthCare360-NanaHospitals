using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HbpProcedures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "hbp_code",
                schema: "billing",
                table: "packages",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "hbp_procedures",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    package_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    specialty_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    specialty = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    package_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    procedure_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    length_of_stay_days = table.Column<int>(type: "integer", nullable: true),
                    reserved_for_public = table.Column<bool>(type: "boolean", nullable: false),
                    stratification = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    master_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_hbp_procedures", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hbp_procedures_code",
                schema: "billing",
                table: "hbp_procedures",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hbp_procedures_is_deleted",
                schema: "billing",
                table: "hbp_procedures",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_hbp_procedures_specialty_code_is_active",
                schema: "billing",
                table: "hbp_procedures",
                columns: new[] { "specialty_code", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hbp_procedures",
                schema: "billing");

            migrationBuilder.DropColumn(
                name: "hbp_code",
                schema: "billing",
                table: "packages");
        }
    }
}
