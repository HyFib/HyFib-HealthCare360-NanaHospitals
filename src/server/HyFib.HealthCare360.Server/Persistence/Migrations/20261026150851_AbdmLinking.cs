using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AbdmLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "interop");

            migrationBuilder.CreateTable(
                name: "abdm_link_requests",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    abha_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    care_contexts_json = table.Column<string>(type: "jsonb", nullable: false),
                    token_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    link_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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
                    table.PrimaryKey("pk_abdm_link_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_link_requests_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "abdm_link_tokens",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hip_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    abha_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    token_cipher = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("pk_abdm_link_tokens", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_link_requests_facility_id",
                schema: "interop",
                table: "abdm_link_requests",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_link_requests_is_deleted",
                schema: "interop",
                table: "abdm_link_requests",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_link_requests_link_request_id",
                schema: "interop",
                table: "abdm_link_requests",
                column: "link_request_id",
                unique: true,
                filter: "link_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_link_requests_patient_id_created_at",
                schema: "interop",
                table: "abdm_link_requests",
                columns: new[] { "patient_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_link_requests_token_request_id",
                schema: "interop",
                table: "abdm_link_requests",
                column: "token_request_id",
                unique: true,
                filter: "token_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_link_tokens_hip_id_abha_address",
                schema: "interop",
                table: "abdm_link_tokens",
                columns: new[] { "hip_id", "abha_address" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_link_tokens_is_deleted",
                schema: "interop",
                table: "abdm_link_tokens",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "abdm_link_requests",
                schema: "interop");

            migrationBuilder.DropTable(
                name: "abdm_link_tokens",
                schema: "interop");
        }
    }
}
