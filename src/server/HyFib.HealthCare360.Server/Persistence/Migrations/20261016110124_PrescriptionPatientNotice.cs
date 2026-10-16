using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrescriptionPatientNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "patient_notified_at",
                schema: "clinical",
                table: "prescriptions",
                type: "timestamp with time zone",
                nullable: true);

            // Out-patient prescriptions signed before this were announced to the patient at signing: a DSC signature
            // added to one now must not announce it again.
            migrationBuilder.Sql("UPDATE clinical.prescriptions SET patient_notified_at = signed_at WHERE signed_at IS NOT NULL AND NOT is_inpatient;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "patient_notified_at",
                schema: "clinical",
                table: "prescriptions");
        }
    }
}
