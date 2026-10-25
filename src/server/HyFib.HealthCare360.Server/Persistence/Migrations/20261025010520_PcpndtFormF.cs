using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PcpndtFormF : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "requires_form_f",
                schema: "diagnostics",
                table: "imaging_studies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "requires_form_f",
                schema: "diagnostics",
                table: "imaging_procedures",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "form_f",
                schema: "diagnostics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imaging_study_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    registration_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    age_years = table.Column<int>(type: "integer", nullable: false),
                    husband_or_father_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    living_sons = table.Column<int>(type: "integer", nullable: false),
                    living_daughters = table.Column<int>(type: "integer", nullable: false),
                    last_menstrual_period = table.Column<DateOnly>(type: "date", nullable: true),
                    weeks_of_pregnancy = table.Column<int>(type: "integer", nullable: true),
                    referral = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    referring_doctor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    referring_doctor_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    indications = table.Column<int[]>(type: "integer[]", nullable: false),
                    other_indication = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    signed_form_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    declared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    declaration_recorded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    procedure_date = table.Column<DateOnly>(type: "date", nullable: true),
                    result_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    result_conveyed_to = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    result_conveyed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    mtp_indicated = table.Column<bool>(type: "boolean", nullable: false),
                    mtp_indication = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    doctor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    doctor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_form_f", x => x.id);
                    table.ForeignKey(
                        name: "fk_form_f_imaging_study_imaging_study_id",
                        column: x => x.imaging_study_id,
                        principalSchema: "diagnostics",
                        principalTable: "imaging_studies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_form_f_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_form_f_facility_id",
                schema: "diagnostics",
                table: "form_f",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_f_facility_id_status_completed_at",
                schema: "diagnostics",
                table: "form_f",
                columns: new[] { "facility_id", "status", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_form_f_form_number",
                schema: "diagnostics",
                table: "form_f",
                column: "form_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_form_f_imaging_study_id",
                schema: "diagnostics",
                table: "form_f",
                column: "imaging_study_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_form_f_is_deleted",
                schema: "diagnostics",
                table: "form_f",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_form_f_patient_id",
                schema: "diagnostics",
                table: "form_f",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "form_f",
                schema: "diagnostics");

            migrationBuilder.DropColumn(
                name: "requires_form_f",
                schema: "diagnostics",
                table: "imaging_studies");

            migrationBuilder.DropColumn(
                name: "requires_form_f",
                schema: "diagnostics",
                table: "imaging_procedures");
        }
    }
}
