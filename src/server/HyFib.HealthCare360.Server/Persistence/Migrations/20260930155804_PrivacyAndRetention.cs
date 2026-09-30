using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "privacy");

            migrationBuilder.CreateTable(
                name: "breaches",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    breach_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    aware_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurred_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    occurred_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    nature = table.Column<List<string>>(type: "text[]", nullable: false),
                    location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    data_categories = table.Column<List<string>>(type: "text[]", nullable: false),
                    estimated_affected = table.Column<int>(type: "integer", nullable: true),
                    consequences = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    mitigation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    principal_steps = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    cause = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    findings = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    remediation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    principal_notice = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    board_informed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    extended_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    report_submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    board_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closing_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_breaches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_requests",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    principal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    contact_email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    requested_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requester_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    corrections = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    identity_check = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    identity_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    identity_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    identity_verified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    response = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    erased_items = table.Column<List<string>>(type: "text[]", nullable: false),
                    retained_until = table.Column<DateOnly>(type: "date", nullable: true),
                    retained_permanently = table.Column<bool>(type: "boolean", nullable: false),
                    summary_snapshot = table.Column<string>(type: "jsonb", nullable: true),
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
                    table.PrimaryKey("pk_data_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_requests_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nominees",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    relationship = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recorded_via = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
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
                    table.PrimaryKey("pk_nominees", x => x.id);
                    table.ForeignKey(
                        name: "fk_nominees_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "retention_changes",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    previous_value = table.Column<int>(type: "integer", nullable: true),
                    value = table.Column<int>(type: "integer", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_changes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "retention_runs",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trigger = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    results = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "breach_affected_patients",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    breach_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notice_channels = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_breach_affected_patients", x => x.id);
                    table.ForeignKey(
                        name: "fk_breach_affected_patients_breach_breach_id",
                        column: x => x.breach_id,
                        principalSchema: "privacy",
                        principalTable: "breaches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_breach_affected_patients_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_request_events",
                schema: "privacy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_request_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_request_events_data_requests_data_request_id",
                        column: x => x.data_request_id,
                        principalSchema: "privacy",
                        principalTable: "data_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_patient_id",
                schema: "ai",
                table: "usage_records",
                column: "patient_id",
                filter: "patient_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_insurance_claims_patient_id",
                schema: "billing",
                table: "insurance_claims",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_recipients_patient_id",
                schema: "engagement",
                table: "campaign_recipients",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_breach_affected_patients_breach_id_patient_id",
                schema: "privacy",
                table: "breach_affected_patients",
                columns: new[] { "breach_id", "patient_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_breach_affected_patients_patient_id",
                schema: "privacy",
                table: "breach_affected_patients",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_breaches_breach_number",
                schema: "privacy",
                table: "breaches",
                column: "breach_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_breaches_is_deleted",
                schema: "privacy",
                table: "breaches",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_breaches_status_aware_at",
                schema: "privacy",
                table: "breaches",
                columns: new[] { "status", "aware_at" });

            migrationBuilder.CreateIndex(
                name: "ix_data_request_events_data_request_id_at",
                schema: "privacy",
                table: "data_request_events",
                columns: new[] { "data_request_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_data_requests_facility_id",
                schema: "privacy",
                table: "data_requests",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_requests_is_deleted",
                schema: "privacy",
                table: "data_requests",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_data_requests_patient_id",
                schema: "privacy",
                table: "data_requests",
                column: "patient_id",
                filter: "patient_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_data_requests_request_number",
                schema: "privacy",
                table: "data_requests",
                column: "request_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_requests_status_due_at",
                schema: "privacy",
                table: "data_requests",
                columns: new[] { "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_nominees_is_deleted",
                schema: "privacy",
                table: "nominees",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_nominees_patient_id",
                schema: "privacy",
                table: "nominees",
                column: "patient_id",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_retention_changes_key_changed_at",
                schema: "privacy",
                table: "retention_changes",
                columns: new[] { "key", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_retention_runs_started_at",
                schema: "privacy",
                table: "retention_runs",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "breach_affected_patients",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "data_request_events",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "nominees",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "retention_changes",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "retention_runs",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "breaches",
                schema: "privacy");

            migrationBuilder.DropTable(
                name: "data_requests",
                schema: "privacy");

            migrationBuilder.DropIndex(
                name: "ix_usage_records_patient_id",
                schema: "ai",
                table: "usage_records");

            migrationBuilder.DropIndex(
                name: "ix_insurance_claims_patient_id",
                schema: "billing",
                table: "insurance_claims");

            migrationBuilder.DropIndex(
                name: "ix_campaign_recipients_patient_id",
                schema: "engagement",
                table: "campaign_recipients");
        }
    }
}
