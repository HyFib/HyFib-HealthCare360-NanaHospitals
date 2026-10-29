using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SexualAssaultExamination : Migration
    {
        /// <summary>
        /// Cases already in the register: a sexual assault on a patient under 18 on the day of the incident (or of the
        /// case, when that is not known) is a POCSO case, as a new entry makes it (EmergencyService.SaveMlcAsync).
        /// </summary>
        public const string MarkChildCases = """
            UPDATE clinical.mlc_records AS m
            SET is_child = true
            FROM patients.patients AS p
            WHERE p.id = m.patient_id
              AND m.cause = 'SexualAssault'
              AND p.date_of_birth IS NOT NULL
              AND p.date_of_birth + interval '18 years' > (coalesce(m.incident_at, m.identified_at) AT TIME ZONE 'Asia/Kolkata')::date;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "attendant_kind",
                schema: "clinical",
                table: "mlc_records",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "attendant_name",
                schema: "clinical",
                table: "mlc_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "consent_given_by",
                schema: "clinical",
                table: "mlc_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "examination_ended_at",
                schema: "clinical",
                table: "mlc_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "examination_started_at",
                schema: "clinical",
                table: "mlc_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "examiner_id",
                schema: "clinical",
                table: "mlc_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "examiner_name",
                schema: "clinical",
                table: "mlc_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "examiner_registration",
                schema: "clinical",
                table: "mlc_records",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "investigating_officer",
                schema: "clinical",
                table: "mlc_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_child",
                schema: "clinical",
                table: "mlc_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "report_forwarded_at",
                schema: "clinical",
                table: "mlc_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reported_to",
                schema: "clinical",
                table: "mlc_records",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(MarkChildCases);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attendant_kind",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "attendant_name",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "consent_given_by",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "examination_ended_at",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "examination_started_at",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "examiner_id",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "examiner_name",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "examiner_registration",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "investigating_officer",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "is_child",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "report_forwarded_at",
                schema: "clinical",
                table: "mlc_records");

            migrationBuilder.DropColumn(
                name: "reported_to",
                schema: "clinical",
                table: "mlc_records");
        }
    }
}
