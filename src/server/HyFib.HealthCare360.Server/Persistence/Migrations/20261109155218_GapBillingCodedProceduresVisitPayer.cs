using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GapBillingCodedProceduresVisitPayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "procedure_codes",
                schema: "billing",
                table: "pre_authorizations",
                newName: "legacy_procedure_text");

            migrationBuilder.CreateTable(
                name: "pre_authorization_procedures",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pre_authorization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pre_authorization_procedures", x => x.id);
                    table.ForeignKey(
                        name: "fk_pre_authorization_procedures_pre_authorizations_pre_authori",
                        column: x => x.pre_authorization_id,
                        principalSchema: "billing",
                        principalTable: "pre_authorizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pre_authorization_procedures_pre_authorization_id_line_numb",
                schema: "billing",
                table: "pre_authorization_procedures",
                columns: new[] { "pre_authorization_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pre_authorization_procedures_system_code",
                schema: "billing",
                table: "pre_authorization_procedures",
                columns: new[] { "system", "code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pre_authorization_procedures",
                schema: "billing");

            migrationBuilder.RenameColumn(
                name: "legacy_procedure_text",
                schema: "billing",
                table: "pre_authorizations",
                newName: "procedure_codes");
        }
    }
}
