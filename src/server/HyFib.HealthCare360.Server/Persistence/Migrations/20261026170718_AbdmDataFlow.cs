using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AbdmDataFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "abdm_consents",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    hip_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    abha_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hiu_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    purpose = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    hi_types = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    date_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    date_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    data_erase_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    care_contexts_json = table.Column<string>(type: "jsonb", nullable: false),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_abdm_consents", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_consents_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "abdm_transfers",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    consent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    hip_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_push_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    key_material_json = table.Column<string>(type: "jsonb", nullable: false),
                    date_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    date_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    results_json = table.Column<string>(type: "jsonb", nullable: true),
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
                    table.PrimaryKey("pk_abdm_transfers", x => x.id);
                    table.ForeignKey(
                        name: "fk_abdm_transfers_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consents_consent_id",
                schema: "interop",
                table: "abdm_consents",
                column: "consent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consents_is_deleted",
                schema: "interop",
                table: "abdm_consents",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_consents_patient_id_notified_at",
                schema: "interop",
                table: "abdm_consents",
                columns: new[] { "patient_id", "notified_at" });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_transfers_is_deleted",
                schema: "interop",
                table: "abdm_transfers",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_abdm_transfers_patient_id_requested_at",
                schema: "interop",
                table: "abdm_transfers",
                columns: new[] { "patient_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_abdm_transfers_transaction_id",
                schema: "interop",
                table: "abdm_transfers",
                column: "transaction_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "abdm_consents",
                schema: "interop");

            migrationBuilder.DropTable(
                name: "abdm_transfers",
                schema: "interop");
        }
    }
}
