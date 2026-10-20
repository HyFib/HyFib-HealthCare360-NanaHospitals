using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrescriptionNoticeHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "patient_notice_held_at",
                schema: "clinical",
                table: "prescriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_prescriptions_patient_notice_held_at",
                schema: "clinical",
                table: "prescriptions",
                column: "patient_notice_held_at",
                filter: "patient_notice_held_at IS NOT NULL");

            // Since PrescriptionPatientNotice, an out-patient prescription signed and not yet announced was held for the
            // doctor's DSC (or its patient has no mobile number, which releasing it finds): hold it, so it is sent if the
            // hospital stops requiring the DSC.
            migrationBuilder.Sql(
                "UPDATE clinical.prescriptions SET patient_notice_held_at = signed_at "
                + "WHERE status = 'Signed' AND patient_notified_at IS NULL AND NOT is_inpatient AND digital_signature IS NULL AND signed_at IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_prescriptions_patient_notice_held_at",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "patient_notice_held_at",
                schema: "clinical",
                table: "prescriptions");
        }
    }
}
