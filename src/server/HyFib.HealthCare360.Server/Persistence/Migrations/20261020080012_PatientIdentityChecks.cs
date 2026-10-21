using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PatientIdentityChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patient_identity_checks",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: true),
                    method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    aadhaar_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    used_virtual_id = table.Column<bool>(type: "boolean", nullable: false),
                    linked_aadhaar = table.Column<bool>(type: "boolean", nullable: false),
                    provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    transaction_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    response_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    device_provider_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    device_model_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    checked_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_patient_identity_checks", x => x.id);
                    table.ForeignKey(
                        name: "fk_patient_identity_checks_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_patient_identity_checks_is_deleted",
                schema: "patients",
                table: "patient_identity_checks",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_patient_identity_checks_patient_id_created_at",
                schema: "patients",
                table: "patient_identity_checks",
                columns: new[] { "patient_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "patient_identity_checks",
                schema: "patients");
        }
    }
}
