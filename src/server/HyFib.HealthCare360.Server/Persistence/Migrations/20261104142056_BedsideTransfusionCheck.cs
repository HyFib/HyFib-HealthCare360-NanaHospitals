using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BedsideTransfusionCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "transfusions",
                schema: "specialtycare",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    component_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    witness_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    witness_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    consent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consent_deferred_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ended_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    volume_ml = table.Column<int>(type: "integer", nullable: true),
                    stop_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_transfusions", x => x.id);
                    table.ForeignKey(
                        name: "fk_transfusions_blood_issues_issue_id",
                        column: x => x.issue_id,
                        principalSchema: "specialtycare",
                        principalTable: "blood_issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfusions_blood_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "specialtycare",
                        principalTable: "blood_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfusions_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transfusion_observations",
                schema: "specialtycare",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfusion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    temperature_c = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    pulse_per_minute = table.Column<int>(type: "integer", nullable: true),
                    systolic_mm_hg = table.Column<int>(type: "integer", nullable: true),
                    diastolic_mm_hg = table.Column<int>(type: "integer", nullable: true),
                    respiratory_rate = table.Column<int>(type: "integer", nullable: true),
                    sp_o2percent = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("pk_transfusion_observations", x => x.id);
                    table.ForeignKey(
                        name: "fk_transfusion_observations_transfusions_transfusion_id",
                        column: x => x.transfusion_id,
                        principalSchema: "specialtycare",
                        principalTable: "transfusions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transfusion_observations_is_deleted",
                schema: "specialtycare",
                table: "transfusion_observations",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_transfusion_observations_transfusion_id_recorded_at",
                schema: "specialtycare",
                table: "transfusion_observations",
                columns: new[] { "transfusion_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_transfusions_facility_id",
                schema: "specialtycare",
                table: "transfusions",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfusions_is_deleted",
                schema: "specialtycare",
                table: "transfusions",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_transfusions_issue_id",
                schema: "specialtycare",
                table: "transfusions",
                column: "issue_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transfusions_patient_id_started_at",
                schema: "specialtycare",
                table: "transfusions",
                columns: new[] { "patient_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_transfusions_request_id",
                schema: "specialtycare",
                table: "transfusions",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfusions_status_request_id",
                schema: "specialtycare",
                table: "transfusions",
                columns: new[] { "status", "request_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transfusion_observations",
                schema: "specialtycare");

            migrationBuilder.DropTable(
                name: "transfusions",
                schema: "specialtycare");
        }
    }
}
