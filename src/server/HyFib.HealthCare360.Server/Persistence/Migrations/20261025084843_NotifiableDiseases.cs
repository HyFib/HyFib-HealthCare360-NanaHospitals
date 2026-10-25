using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotifiableDiseases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idsp_weekly_reports",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    form = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reported_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    case_count = table.Column<int>(type: "integer", nullable: false),
                    reported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("pk_idsp_weekly_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifiable_cases",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    disease_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    onset_date = table.Column<DateOnly>(type: "date", nullable: true),
                    presumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    presumed_week = table.Column<int>(type: "integer", nullable: true),
                    diagnosis_id = table.Column<Guid>(type: "uuid", nullable: true),
                    icd_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_week = table.Column<int>(type: "integer", nullable: true),
                    lab_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    test_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    test_result = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    alerted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    discard_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    discarded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    discarded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_notifiable_cases", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifiable_cases_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idsp_weekly_reports_facility_id",
                schema: "clinical",
                table: "idsp_weekly_reports",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_idsp_weekly_reports_facility_id_year_week_form",
                schema: "clinical",
                table: "idsp_weekly_reports",
                columns: new[] { "facility_id", "year", "week", "form" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idsp_weekly_reports_is_deleted",
                schema: "clinical",
                table: "idsp_weekly_reports",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_notifiable_cases_diagnosis_id",
                schema: "clinical",
                table: "notifiable_cases",
                column: "diagnosis_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifiable_cases_facility_id",
                schema: "clinical",
                table: "notifiable_cases",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifiable_cases_facility_id_confirmed_week",
                schema: "clinical",
                table: "notifiable_cases",
                columns: new[] { "facility_id", "confirmed_week" });

            migrationBuilder.CreateIndex(
                name: "ix_notifiable_cases_facility_id_presumed_week",
                schema: "clinical",
                table: "notifiable_cases",
                columns: new[] { "facility_id", "presumed_week" });

            migrationBuilder.CreateIndex(
                name: "ix_notifiable_cases_is_deleted",
                schema: "clinical",
                table: "notifiable_cases",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_notifiable_cases_lab_result_id",
                schema: "clinical",
                table: "notifiable_cases",
                column: "lab_result_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifiable_cases_patient_id_disease_code",
                schema: "clinical",
                table: "notifiable_cases",
                columns: new[] { "patient_id", "disease_code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idsp_weekly_reports",
                schema: "clinical");

            migrationBuilder.DropTable(
                name: "notifiable_cases",
                schema: "clinical");
        }
    }
}
