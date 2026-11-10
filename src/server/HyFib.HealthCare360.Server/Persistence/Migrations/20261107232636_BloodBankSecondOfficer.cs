using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <summary>
    /// The blood bank's second officer (maker-checker, slice 3): who entered a donation's screening results and who verified
    /// them before its units left quarantine, and whether one officer did both where the facility allows it; who grouped a
    /// request's sample and who cross-matched each unit, by account; the group of the patient's earlier requests a grouping
    /// disagreed with, and the new sample that replaced it; the doctor's account and practitioner record that authorised an
    /// uncross-matched release; and an issue made on one officer's checks, for the medical officer's review.
    /// </summary>
    public partial class BloodBankSecondOfficer : Migration
    {
        /// <summary>
        /// Donations whose units were released before a second officer was required (all five markers non-reactive): their
        /// results count as verified when they were entered, with no officer named, so they are not shown as waiting and a
        /// component separated from them later goes to stock as before.
        /// </summary>
        public const string MarkReleasedDonationsVerified = """
            UPDATE specialtycare.donations
            SET verified_at = lab_results_at
            WHERE lab_results_at IS NOT NULL AND verified_at IS NULL AND status <> 'Discarded' AND blood_group <> 'Unknown'
              AND tti_hiv = 'NonReactive' AND tti_hbs_ag = 'NonReactive' AND tti_hcv = 'NonReactive'
              AND tti_syphilis = 'NonReactive' AND tti_malaria = 'NonReactive';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "lab_results_by_name",
                schema: "specialtycare",
                table: "donations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lab_results_by_user_id",
                schema: "specialtycare",
                table: "donations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_at",
                schema: "specialtycare",
                table: "donations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "verified_by_enterer",
                schema: "specialtycare",
                table: "donations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "verified_by_name",
                schema: "specialtycare",
                table: "donations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "verified_by_user_id",
                schema: "specialtycare",
                table: "donations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "performed_by_user_id",
                schema: "specialtycare",
                table: "crossmatches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "earlier_group",
                schema: "specialtycare",
                table: "blood_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "emergency_release_by_practitioner_id",
                schema: "specialtycare",
                table: "blood_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "emergency_release_by_user_id",
                schema: "specialtycare",
                table: "blood_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_sample_group",
                schema: "specialtycare",
                table: "blood_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "grouped_by_user_id",
                schema: "specialtycare",
                table: "blood_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "regrouped_at",
                schema: "specialtycare",
                table: "blood_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "issued_on_own_checks",
                schema: "specialtycare",
                table: "blood_issues",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(MarkReleasedDonationsVerified);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "lab_results_by_name",
                schema: "specialtycare",
                table: "donations");

            migrationBuilder.DropColumn(
                name: "lab_results_by_user_id",
                schema: "specialtycare",
                table: "donations");

            migrationBuilder.DropColumn(
                name: "verified_at",
                schema: "specialtycare",
                table: "donations");

            migrationBuilder.DropColumn(
                name: "verified_by_enterer",
                schema: "specialtycare",
                table: "donations");

            migrationBuilder.DropColumn(
                name: "verified_by_name",
                schema: "specialtycare",
                table: "donations");

            migrationBuilder.DropColumn(
                name: "verified_by_user_id",
                schema: "specialtycare",
                table: "donations");

            migrationBuilder.DropColumn(
                name: "performed_by_user_id",
                schema: "specialtycare",
                table: "crossmatches");

            migrationBuilder.DropColumn(
                name: "earlier_group",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "emergency_release_by_practitioner_id",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "emergency_release_by_user_id",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "first_sample_group",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "grouped_by_user_id",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "regrouped_at",
                schema: "specialtycare",
                table: "blood_requests");

            migrationBuilder.DropColumn(
                name: "issued_on_own_checks",
                schema: "specialtycare",
                table: "blood_issues");
        }
    }
}
