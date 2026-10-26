using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AbhaVerifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "abdm_abha_verifications",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    txn_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sent_message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    code_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    abha_number_masked = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    abha_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    gender = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    year_of_birth = table.Column<int>(type: "integer", nullable: true),
                    kyc_verified = table.Column<bool>(type: "boolean", nullable: true),
                    differences_json = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_abdm_abha_verifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_abha_verifications_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_abha_verifications_is_deleted",
                schema: "interop",
                table: "abdm_abha_verifications",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_abha_verifications_patient_id_created_at",
                schema: "interop",
                table: "abdm_abha_verifications",
                columns: new[] { "patient_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "abdm_abha_verifications",
                schema: "interop");
        }
    }
}
