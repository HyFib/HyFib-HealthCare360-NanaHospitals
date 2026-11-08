using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InpatientNews2TransfersCarePlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "consciousness",
                schema: "clinical",
                table: "vital_signs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sp_o2scale",
                schema: "clinical",
                table: "vital_signs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "care_plan_problems",
                schema: "inpatient",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    problem_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    goal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    interventions_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_by = table.Column<Guid>(type: "uuid", nullable: true),
                    started_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_care_plan_problems", x => x.id);
                    table.ForeignKey(
                        name: "fk_care_plan_problems_admissions_admission_id",
                        column: x => x.admission_id,
                        principalSchema: "inpatient",
                        principalTable: "admissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "early_warning_escalations",
                schema: "inpatient",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ward_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bed_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vital_signs_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vitals_recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vitals_recorded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    score = table.Column<int>(type: "integer", nullable: false),
                    any_single_parameter_three = table.Column<bool>(type: "boolean", nullable: false),
                    is_score_complete = table.Column<bool>(type: "boolean", nullable: false),
                    summary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    level = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consultant_practitioner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    responded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    responded_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    responded_by_doctor = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    response = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
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
                    table.PrimaryKey("pk_early_warning_escalations", x => x.id);
                    table.ForeignKey(
                        name: "fk_early_warning_escalations_admissions_admission_id",
                        column: x => x.admission_id,
                        principalSchema: "inpatient",
                        principalTable: "admissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transfer_requests",
                schema: "inpatient",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_ward_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_bed_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_ward_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_bed_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    clinical_summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    consultant_practitioner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_urgent = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    hold_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    transferred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    overdue_notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_transfer_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_transfer_requests_admissions_admission_id",
                        column: x => x.admission_id,
                        principalSchema: "inpatient",
                        principalTable: "admissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfer_requests_beds_to_bed_id",
                        column: x => x.to_bed_id,
                        principalSchema: "inpatient",
                        principalTable: "beds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "care_plan_evaluations",
                schema: "inpatient",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    problem_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    duty_date = table.Column<DateOnly>(type: "date", nullable: false),
                    shift = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    progress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evaluated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    evaluated_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_care_plan_evaluations", x => x.id);
                    table.ForeignKey(
                        name: "fk_care_plan_evaluations_care_plan_problem_problem_id",
                        column: x => x.problem_id,
                        principalSchema: "inpatient",
                        principalTable: "care_plan_problems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_care_plan_evaluations_admission_id",
                schema: "inpatient",
                table: "care_plan_evaluations",
                column: "admission_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_plan_evaluations_facility_id",
                schema: "inpatient",
                table: "care_plan_evaluations",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_plan_evaluations_is_deleted",
                schema: "inpatient",
                table: "care_plan_evaluations",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_care_plan_evaluations_problem_id_duty_date_shift",
                schema: "inpatient",
                table: "care_plan_evaluations",
                columns: new[] { "problem_id", "duty_date", "shift" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_care_plan_problems_admission_id_status",
                schema: "inpatient",
                table: "care_plan_problems",
                columns: new[] { "admission_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_care_plan_problems_facility_id",
                schema: "inpatient",
                table: "care_plan_problems",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_plan_problems_is_deleted",
                schema: "inpatient",
                table: "care_plan_problems",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_care_plan_problems_patient_id",
                schema: "inpatient",
                table: "care_plan_problems",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_early_warning_escalations_admission_id_status_level",
                schema: "inpatient",
                table: "early_warning_escalations",
                columns: new[] { "admission_id", "status", "level" });

            migrationBuilder.CreateIndex(
                name: "ix_early_warning_escalations_facility_id",
                schema: "inpatient",
                table: "early_warning_escalations",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_early_warning_escalations_is_deleted",
                schema: "inpatient",
                table: "early_warning_escalations",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_early_warning_escalations_patient_id",
                schema: "inpatient",
                table: "early_warning_escalations",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_early_warning_escalations_ward_id_status",
                schema: "inpatient",
                table: "early_warning_escalations",
                columns: new[] { "ward_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_transfer_requests_facility_id",
                schema: "inpatient",
                table: "transfer_requests",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_requests_from_ward_id_status",
                schema: "inpatient",
                table: "transfer_requests",
                columns: new[] { "from_ward_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_transfer_requests_is_deleted",
                schema: "inpatient",
                table: "transfer_requests",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_requests_patient_id",
                schema: "inpatient",
                table: "transfer_requests",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_requests_to_bed_id",
                schema: "inpatient",
                table: "transfer_requests",
                column: "to_bed_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_requests_to_ward_id_status",
                schema: "inpatient",
                table: "transfer_requests",
                columns: new[] { "to_ward_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_transfer_requests_pending_admission",
                schema: "inpatient",
                table: "transfer_requests",
                column: "admission_id",
                unique: true,
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "care_plan_evaluations",
                schema: "inpatient");

            migrationBuilder.DropTable(
                name: "early_warning_escalations",
                schema: "inpatient");

            migrationBuilder.DropTable(
                name: "transfer_requests",
                schema: "inpatient");

            migrationBuilder.DropTable(
                name: "care_plan_problems",
                schema: "inpatient");

            migrationBuilder.DropColumn(
                name: "consciousness",
                schema: "clinical",
                table: "vital_signs");

            migrationBuilder.DropColumn(
                name: "sp_o2scale",
                schema: "clinical",
                table: "vital_signs");
        }
    }
}
