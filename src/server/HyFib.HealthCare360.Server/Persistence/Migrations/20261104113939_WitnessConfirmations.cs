using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WitnessConfirmations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "witness_user_id",
                schema: "inpatient",
                table: "medication_administrations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "witness_confirmations",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    witness_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    witness_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    for_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_for_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    used_for_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_witness_confirmations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_witness_confirmations_used_for_type_used_for_id",
                schema: "identity",
                table: "witness_confirmations",
                columns: new[] { "used_for_type", "used_for_id" });

            migrationBuilder.CreateIndex(
                name: "ix_witness_confirmations_witness_user_id",
                schema: "identity",
                table: "witness_confirmations",
                column: "witness_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "witness_confirmations",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "witness_user_id",
                schema: "inpatient",
                table: "medication_administrations");
        }
    }
}
