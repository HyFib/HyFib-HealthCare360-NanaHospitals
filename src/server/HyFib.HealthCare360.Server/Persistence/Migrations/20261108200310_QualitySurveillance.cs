using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QualitySurveillance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "computed_by",
                schema: "quality",
                table: "quality_indicators",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "computed_at",
                schema: "quality",
                table: "indicator_values",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_computed",
                schema: "quality",
                table: "indicator_values",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "source_module",
                schema: "quality",
                table: "incidents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_record_id",
                schema: "quality",
                table: "incidents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_record_type",
                schema: "quality",
                table: "incidents",
                type: "character varying(48)",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_reference",
                schema: "quality",
                table: "incidents",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_templates",
                schema: "quality",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    chapter = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    items = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("pk_audit_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_episodes",
                schema: "quality",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ward_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inserted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    inserted_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removal_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    removed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
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
                    table.PrimaryKey("pk_device_episodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_episodes_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nursing_safety_events",
                schema: "inpatient",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ward_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    location = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    immediate_action = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    fall_injury = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    witnessed = table.Column<bool>(type: "boolean", nullable: true),
                    stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    body_site = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    present_on_admission = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    incident_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
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
                    table.PrimaryKey("pk_nursing_safety_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_nursing_safety_events_admissions_admission_id",
                        column: x => x.admission_id,
                        principalSchema: "inpatient",
                        principalTable: "admissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audits",
                schema: "quality",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audit_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    chapter = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    area = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    scheduled_on = table.Column<DateOnly>(type: "date", nullable: false),
                    lead_auditor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recurrence = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    next_audit_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_audits", x => x.id);
                    table.ForeignKey(
                        name: "fk_audits_audit_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "quality",
                        principalTable: "audit_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hai_cases",
                schema: "quality",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    origin = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    culture_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    organism = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    specimen = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    specimen_collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    surgery_booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    surgery_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    operated_on = table.Column<DateOnly>(type: "date", nullable: true),
                    event_on = table.Column<DateOnly>(type: "date", nullable: false),
                    criteria_met = table.Column<List<string>>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
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
                    table.PrimaryKey("pk_hai_cases", x => x.id);
                    table.ForeignKey(
                        name: "fk_hai_cases_device_episodes_device_episode_id",
                        column: x => x.device_episode_id,
                        principalSchema: "quality",
                        principalTable: "device_episodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hai_cases_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_findings",
                schema: "quality",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    item_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    item_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    result = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    observation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    capa_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_findings_audits_audit_id",
                        column: x => x.audit_id,
                        principalSchema: "quality",
                        principalTable: "audits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_audit_findings_capa_capa_id",
                        column: x => x.capa_id,
                        principalSchema: "quality",
                        principalTable: "capas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_source_record_type_source_record_id",
                schema: "quality",
                table: "incidents",
                columns: new[] { "source_record_type", "source_record_id" },
                unique: true,
                filter: "source_record_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_findings_audit_id_item_key",
                schema: "quality",
                table: "audit_findings",
                columns: new[] { "audit_id", "item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_findings_capa_id",
                schema: "quality",
                table: "audit_findings",
                column: "capa_id",
                filter: "capa_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_templates_code",
                schema: "quality",
                table: "audit_templates",
                column: "code",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_audit_templates_is_deleted",
                schema: "quality",
                table: "audit_templates",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_audits_audit_number",
                schema: "quality",
                table: "audits",
                column: "audit_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audits_facility_id",
                schema: "quality",
                table: "audits",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_audits_facility_id_status_scheduled_on",
                schema: "quality",
                table: "audits",
                columns: new[] { "facility_id", "status", "scheduled_on" });

            migrationBuilder.CreateIndex(
                name: "ix_audits_is_deleted",
                schema: "quality",
                table: "audits",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_audits_lead_auditor_user_id",
                schema: "quality",
                table: "audits",
                column: "lead_auditor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audits_template_id",
                schema: "quality",
                table: "audits",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_episodes_admission_id",
                schema: "quality",
                table: "device_episodes",
                column: "admission_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_episodes_facility_id",
                schema: "quality",
                table: "device_episodes",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_episodes_facility_id_device_inserted_at",
                schema: "quality",
                table: "device_episodes",
                columns: new[] { "facility_id", "device", "inserted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_device_episodes_is_deleted",
                schema: "quality",
                table: "device_episodes",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_device_episodes_patient_id_device",
                schema: "quality",
                table: "device_episodes",
                columns: new[] { "patient_id", "device" });

            migrationBuilder.CreateIndex(
                name: "ix_hai_cases_case_number",
                schema: "quality",
                table: "hai_cases",
                column: "case_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hai_cases_culture_result_id_type",
                schema: "quality",
                table: "hai_cases",
                columns: new[] { "culture_result_id", "type" },
                unique: true,
                filter: "culture_result_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_hai_cases_device_episode_id",
                schema: "quality",
                table: "hai_cases",
                column: "device_episode_id");

            migrationBuilder.CreateIndex(
                name: "ix_hai_cases_facility_id",
                schema: "quality",
                table: "hai_cases",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_hai_cases_facility_id_status_event_on",
                schema: "quality",
                table: "hai_cases",
                columns: new[] { "facility_id", "status", "event_on" });

            migrationBuilder.CreateIndex(
                name: "ix_hai_cases_facility_id_type_event_on",
                schema: "quality",
                table: "hai_cases",
                columns: new[] { "facility_id", "type", "event_on" });

            migrationBuilder.CreateIndex(
                name: "ix_hai_cases_is_deleted",
                schema: "quality",
                table: "hai_cases",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_hai_cases_patient_id",
                schema: "quality",
                table: "hai_cases",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_nursing_safety_events_admission_id_occurred_at",
                schema: "inpatient",
                table: "nursing_safety_events",
                columns: new[] { "admission_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_nursing_safety_events_facility_id",
                schema: "inpatient",
                table: "nursing_safety_events",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_nursing_safety_events_facility_id_kind_occurred_at",
                schema: "inpatient",
                table: "nursing_safety_events",
                columns: new[] { "facility_id", "kind", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_nursing_safety_events_is_deleted",
                schema: "inpatient",
                table: "nursing_safety_events",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_nursing_safety_events_patient_id",
                schema: "inpatient",
                table: "nursing_safety_events",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_findings",
                schema: "quality");

            migrationBuilder.DropTable(
                name: "hai_cases",
                schema: "quality");

            migrationBuilder.DropTable(
                name: "nursing_safety_events",
                schema: "inpatient");

            migrationBuilder.DropTable(
                name: "audits",
                schema: "quality");

            migrationBuilder.DropTable(
                name: "device_episodes",
                schema: "quality");

            migrationBuilder.DropTable(
                name: "audit_templates",
                schema: "quality");

            migrationBuilder.DropIndex(
                name: "ix_incidents_source_record_type_source_record_id",
                schema: "quality",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "computed_by",
                schema: "quality",
                table: "quality_indicators");

            migrationBuilder.DropColumn(
                name: "computed_at",
                schema: "quality",
                table: "indicator_values");

            migrationBuilder.DropColumn(
                name: "is_computed",
                schema: "quality",
                table: "indicator_values");

            migrationBuilder.DropColumn(
                name: "source_module",
                schema: "quality",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "source_record_id",
                schema: "quality",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "source_record_type",
                schema: "quality",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "source_reference",
                schema: "quality",
                table: "incidents");
        }
    }
}
