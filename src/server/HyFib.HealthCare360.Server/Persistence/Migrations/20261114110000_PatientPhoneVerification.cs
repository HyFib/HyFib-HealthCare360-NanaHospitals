using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PatientPhoneVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "phone_verified_at",
                schema: "patients",
                table: "patients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone_verified_number",
                schema: "patients",
                table: "patients",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Every code sent before this one changed a number.
            migrationBuilder.AddColumn<string>(
                name: "purpose",
                schema: "patients",
                table: "patient_phone_changes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Change");

            migrationBuilder.CreateTable(
                name: "portal_record_confirmations",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    confirmed = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_portal_record_confirmations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_portal_record_confirmations_is_deleted",
                schema: "patients",
                table: "portal_record_confirmations",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_portal_record_confirmations_account_phone_created_at",
                schema: "patients",
                table: "portal_record_confirmations",
                columns: new[] { "account_phone", "created_at" });

            // A number a patient was given with a code sent to it, and still has, was verified then: the patient read the
            // code out at the desk. Every other number starts unverified.
            migrationBuilder.Sql("""
                UPDATE patients.patients AS p
                SET phone_verified_at = c.confirmed_at,
                    phone_verified_number = c.phone
                FROM (
                    SELECT DISTINCT ON (patient_id) patient_id, phone, confirmed_at
                    FROM patients.patient_phone_changes
                    WHERE status = 'Confirmed' AND confirmed_at IS NOT NULL AND is_deleted = false
                    ORDER BY patient_id, confirmed_at DESC
                ) AS c
                WHERE p.id = c.patient_id AND p.phone = c.phone;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous version reads every code as a number change: a verification still waiting for its code ends, so
            // it can never be confirmed as a change to the same number. The rows stay, as the history they are.
            migrationBuilder.Sql("UPDATE patients.patient_phone_changes SET status = 'Superseded' WHERE purpose = 'Verify' AND status = 'Pending';");

            migrationBuilder.DropTable(
                name: "portal_record_confirmations",
                schema: "patients");

            migrationBuilder.DropColumn(
                name: "purpose",
                schema: "patients",
                table: "patient_phone_changes");

            migrationBuilder.DropColumn(
                name: "phone_verified_at",
                schema: "patients",
                table: "patients");

            migrationBuilder.DropColumn(
                name: "phone_verified_number",
                schema: "patients",
                table: "patients");
        }
    }
}
