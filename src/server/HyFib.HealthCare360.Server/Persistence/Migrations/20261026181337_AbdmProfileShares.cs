using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AbdmProfileShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "abdm_profile_shares",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hip_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    context = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_date = table.Column<DateOnly>(type: "date", nullable: false),
                    token_number = table.Column<int>(type: "integer", nullable: false),
                    abha_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    abha_number_cipher = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    abha_number_masked = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    gender = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    year_of_birth = table.Column<int>(type: "integer", nullable: true),
                    month_of_birth = table.Column<int>(type: "integer", nullable: true),
                    day_of_birth = table.Column<int>(type: "integer", nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    address_line = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    district = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    pincode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    shared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    matched_by = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_abdm_profile_shares", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_profile_shares_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_profile_shares_hip_id_abha_address_context_shared_at",
                schema: "interop",
                table: "abdm_profile_shares",
                columns: new[] { "hip_id", "abha_address", "context", "shared_at" });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_profile_shares_hip_id_token_date_token_number",
                schema: "interop",
                table: "abdm_profile_shares",
                columns: new[] { "hip_id", "token_date", "token_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_abdm_profile_shares_is_deleted",
                schema: "interop",
                table: "abdm_profile_shares",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_profile_shares_patient_id_shared_at",
                schema: "interop",
                table: "abdm_profile_shares",
                columns: new[] { "patient_id", "shared_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "abdm_profile_shares",
                schema: "interop");
        }
    }
}
