using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChargeRefusals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "charge_refusals",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    service_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    first_refused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_refused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charge_refusals", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_charge_refusals_admission_id",
                schema: "billing",
                table: "charge_refusals",
                column: "admission_id");

            migrationBuilder.CreateIndex(
                name: "ix_charge_refusals_source_module_source_reference",
                schema: "billing",
                table: "charge_refusals",
                columns: new[] { "source_module", "source_reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "charge_refusals",
                schema: "billing");
        }
    }
}
