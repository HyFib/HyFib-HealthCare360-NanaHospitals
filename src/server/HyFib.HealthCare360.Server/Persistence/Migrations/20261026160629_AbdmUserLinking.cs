using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AbdmUserLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "abdm_care_contexts",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hip_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    abha_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    record_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    route = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("pk_abdm_care_contexts", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_care_contexts_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "abdm_discoveries",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    hip_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    abha_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    matched_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    offered_json = table.Column<string>(type: "jsonb", nullable: false),
                    selected_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    offered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    link_reference = table.Column<Guid>(type: "uuid", nullable: true),
                    code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    code_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    code_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_abdm_discoveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_discoveries_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_care_contexts_hip_id_abha_address_reference_number",
                schema: "interop",
                table: "abdm_care_contexts",
                columns: new[] { "hip_id", "abha_address", "reference_number" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_care_contexts_is_deleted",
                schema: "interop",
                table: "abdm_care_contexts",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_care_contexts_patient_id",
                schema: "interop",
                table: "abdm_care_contexts",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_discoveries_is_deleted",
                schema: "interop",
                table: "abdm_discoveries",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_discoveries_link_reference",
                schema: "interop",
                table: "abdm_discoveries",
                column: "link_reference",
                unique: true,
                filter: "link_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_discoveries_patient_id_created_at",
                schema: "interop",
                table: "abdm_discoveries",
                columns: new[] { "patient_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_discoveries_transaction_id",
                schema: "interop",
                table: "abdm_discoveries",
                column: "transaction_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "abdm_care_contexts",
                schema: "interop");

            migrationBuilder.DropTable(
                name: "abdm_discoveries",
                schema: "interop");
        }
    }
}
