using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AbdmHealthInformation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "abdm_health_information_requests",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    artefact_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    private_key_cipher = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    nonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_abdm_health_information_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_health_information_requests_abdm_consent_artefacts_art",
                        column: x => x.artefact_entity_id,
                        principalSchema: "interop",
                        principalTable: "abdm_consent_artefacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_abdm_health_information_requests_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "abdm_received_records",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    care_context_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: false),
                    entry_index = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    record_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checksum_matches = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_abdm_received_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_received_records_abdm_health_information_requests_requ",
                        column: x => x.request_entity_id,
                        principalSchema: "interop",
                        principalTable: "abdm_health_information_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_abdm_received_records_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_health_information_requests_artefact_entity_id",
                schema: "interop",
                table: "abdm_health_information_requests",
                column: "artefact_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_health_information_requests_gateway_request_id",
                schema: "interop",
                table: "abdm_health_information_requests",
                column: "gateway_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_abdm_health_information_requests_is_deleted",
                schema: "interop",
                table: "abdm_health_information_requests",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_health_information_requests_patient_id",
                schema: "interop",
                table: "abdm_health_information_requests",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_health_information_requests_transaction_id",
                schema: "interop",
                table: "abdm_health_information_requests",
                column: "transaction_id",
                unique: true,
                filter: "transaction_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_received_records_is_deleted",
                schema: "interop",
                table: "abdm_received_records",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_received_records_patient_id",
                schema: "interop",
                table: "abdm_received_records",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_received_records_request_entity_id_page_number_entry_i",
                schema: "interop",
                table: "abdm_received_records",
                columns: new[] { "request_entity_id", "page_number", "entry_index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "abdm_received_records",
                schema: "interop");

            migrationBuilder.DropTable(
                name: "abdm_health_information_requests",
                schema: "interop");
        }
    }
}
