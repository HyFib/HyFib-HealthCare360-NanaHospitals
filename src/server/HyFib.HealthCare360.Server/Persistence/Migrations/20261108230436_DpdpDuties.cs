using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DpdpDuties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consent_manager_events",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_manager_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    artefact_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent_manager_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "consent_managers",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    registration_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    secret_cipher = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    is_simulator = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_consent_managers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_audits",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_from = table.Column<DateOnly>(type: "date", nullable: false),
                    period_to = table.Column<DateOnly>(type: "date", nullable: false),
                    auditor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    findings = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    significant_observations = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    completed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    reported_to_board_on = table.Column<DateOnly>(type: "date", nullable: true),
                    report_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    next_due = table.Column<DateOnly>(type: "date", nullable: false),
                    reminded_days_before = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("pk_data_audits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dpias",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    activity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    data_categories = table.Column<List<string>>(type: "text[]", nullable: false),
                    data_principals = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    purpose = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    risks = table.Column<string>(type: "jsonb", nullable: false),
                    mitigations = table.Column<string>(type: "jsonb", nullable: false),
                    review_due = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reminded_days_before = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("pk_dpias", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "erasure_replays",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ledger_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    erased_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    replayed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied = table.Column<List<string>>(type: "text[]", nullable: false),
                    held_back = table.Column<List<string>>(type: "text[]", nullable: false),
                    patient_missing = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erasure_replays", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "guardian_consents",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guardian_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    relationship = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    guardian_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    guardian_is_adult = table.Column<bool>(type: "boolean", nullable: false),
                    method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    document_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    document_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    code_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
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
                    table.PrimaryKey("pk_guardian_consents", x => x.id);
                    table.ForeignKey(
                        name: "fk_guardian_consents_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consent_artefacts",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_manager_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artefact_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data_items = table.Column<List<string>>(type: "text[]", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consent_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    given_by_guardian = table.Column<bool>(type: "boolean", nullable: false),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_via = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    withdrawal_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawal_attempts = table.Column<int>(type: "integer", nullable: false),
                    withdrawal_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_consent_artefacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_consent_artefacts_consent_manager_consent_manager_id",
                        column: x => x.consent_manager_id,
                        principalSchema: "privacy",
                        principalTable: "consent_managers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consent_artefacts_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_consent_artefacts_consent_manager_id_artefact_id",
                schema: "privacy",
                table: "consent_artefacts",
                columns: new[] { "consent_manager_id", "artefact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_consent_artefacts_is_deleted",
                schema: "privacy",
                table: "consent_artefacts",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_consent_artefacts_patient_id",
                schema: "privacy",
                table: "consent_artefacts",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_consent_artefacts_status_valid_until",
                schema: "privacy",
                table: "consent_artefacts",
                columns: new[] { "status", "valid_until" });

            migrationBuilder.CreateIndex(
                name: "ix_consent_manager_events_consent_manager_id_event_id",
                schema: "privacy",
                table: "consent_manager_events",
                columns: new[] { "consent_manager_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_consent_managers_code",
                schema: "privacy",
                table: "consent_managers",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_consent_managers_is_deleted",
                schema: "privacy",
                table: "consent_managers",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_data_audits_is_deleted",
                schema: "privacy",
                table: "data_audits",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_data_audits_period_to",
                schema: "privacy",
                table: "data_audits",
                column: "period_to");

            migrationBuilder.CreateIndex(
                name: "ix_dpias_is_deleted",
                schema: "privacy",
                table: "dpias",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_dpias_number",
                schema: "privacy",
                table: "dpias",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dpias_status_review_due",
                schema: "privacy",
                table: "dpias",
                columns: new[] { "status", "review_due" });

            migrationBuilder.CreateIndex(
                name: "ix_erasure_replays_ledger_entry_id",
                schema: "privacy",
                table: "erasure_replays",
                column: "ledger_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erasure_replays_patient_id",
                schema: "privacy",
                table: "erasure_replays",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_erasure_replays_replayed_at",
                schema: "privacy",
                table: "erasure_replays",
                column: "replayed_at");

            migrationBuilder.CreateIndex(
                name: "ix_guardian_consents_guardian_phone_created_at",
                schema: "privacy",
                table: "guardian_consents",
                columns: new[] { "guardian_phone", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_guardian_consents_is_deleted",
                schema: "privacy",
                table: "guardian_consents",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_guardian_consents_patient_id_status",
                schema: "privacy",
                table: "guardian_consents",
                columns: new[] { "patient_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_guardian_consents_verified",
                schema: "privacy",
                table: "guardian_consents",
                column: "patient_id",
                unique: true,
                filter: "status = 'Verified' AND is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consent_artefacts",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "consent_manager_events",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "data_audits",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "dpias",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "erasure_replays",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "guardian_consents",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "consent_managers",
                schema: "privacy");
        }
    }
}
