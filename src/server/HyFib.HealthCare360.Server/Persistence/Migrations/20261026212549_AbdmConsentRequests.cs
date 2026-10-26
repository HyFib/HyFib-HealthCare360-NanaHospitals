using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AbdmConsentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "abdm_consent_requests",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    abha_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    gateway_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hi_types = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    date_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    date_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    data_erase_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requester_practitioner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_abdm_consent_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_consent_requests_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "abdm_consent_artefacts",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_request_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artefact_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hip_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    hip_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    care_contexts_json = table.Column<string>(type: "jsonb", nullable: false),
                    hi_types = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    date_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    date_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    data_erase_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fetch_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_abdm_consent_artefacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_consent_artefacts_abdm_consent_request_consent_request",
                        column: x => x.consent_request_entity_id,
                        principalSchema: "interop",
                        principalTable: "abdm_consent_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_abdm_consent_artefacts_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consent_artefacts_artefact_id",
                schema: "interop",
                table: "abdm_consent_artefacts",
                column: "artefact_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consent_artefacts_consent_request_entity_id",
                schema: "interop",
                table: "abdm_consent_artefacts",
                column: "consent_request_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consent_artefacts_fetch_request_id",
                schema: "interop",
                table: "abdm_consent_artefacts",
                column: "fetch_request_id",
                unique: true,
                filter: "fetch_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consent_artefacts_is_deleted",
                schema: "interop",
                table: "abdm_consent_artefacts",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consent_artefacts_patient_id_created_at",
                schema: "interop",
                table: "abdm_consent_artefacts",
                columns: new[] { "patient_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consent_requests_consent_request_id",
                schema: "interop",
                table: "abdm_consent_requests",
                column: "consent_request_id",
                unique: true,
                filter: "consent_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consent_requests_gateway_request_id",
                schema: "interop",
                table: "abdm_consent_requests",
                column: "gateway_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consent_requests_is_deleted",
                schema: "interop",
                table: "abdm_consent_requests",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consent_requests_patient_id_created_at",
                schema: "interop",
                table: "abdm_consent_requests",
                columns: new[] { "patient_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "abdm_consent_artefacts",
                schema: "interop");

            migrationBuilder.DropTable(
                name: "abdm_consent_requests",
                schema: "interop");
        }
    }
}
