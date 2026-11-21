using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRestraintAlertMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "check_alerted_for_due",
                schema: "inpatient",
                table: "restraint_episodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lapse_alerted_for",
                schema: "inpatient",
                table: "restraint_episodes",
                type: "timestamp with time zone",
                nullable: true);

            // Before restraints followed the patient, a discharge or a cancelled admission left the restraint Active for
            // ever. Those are released now, at the time the stay ended and with a recorded reason, so they leave the ward
            // lists and raise no overdue-check or lapse alerts.
            migrationBuilder.Sql("""
                UPDATE inpatient.restraint_episodes AS r
                SET status = 'Released',
                    released_at = coalesce(a.discharged_at, a.cancelled_at),
                    released_by_name = 'HC360 update',
                    release_reason = 'The stay had ended with the restraint still recorded as active; released when HC360 was updated.',
                    updated_at = now()
                FROM inpatient.admissions AS a
                WHERE r.admission_id = a.id
                  AND r.status = 'Active'
                  AND (a.discharged_at IS NOT NULL OR a.cancelled_at IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "check_alerted_for_due",
                schema: "inpatient",
                table: "restraint_episodes");

            migrationBuilder.DropColumn(
                name: "lapse_alerted_for",
                schema: "inpatient",
                table: "restraint_episodes");
        }
    }
}
