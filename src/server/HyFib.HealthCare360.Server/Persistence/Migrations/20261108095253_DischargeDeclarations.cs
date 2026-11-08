using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DischargeDeclarations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "lama_doctor_name",
                schema: "clinical",
                table: "emergency_cases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lama_doctor_user_id",
                schema: "clinical",
                table: "emergency_cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lama_relationship",
                schema: "clinical",
                table: "emergency_cases",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lama_signatory_name",
                schema: "clinical",
                table: "emergency_cases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lama_signed_by",
                schema: "clinical",
                table: "emergency_cases",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lama_signed_form_file_id",
                schema: "clinical",
                table: "emergency_cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lama_witness_name",
                schema: "clinical",
                table: "emergency_cases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lama_witness_user_id",
                schema: "clinical",
                table: "emergency_cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discharge_declarations",
                schema: "inpatient",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    signed_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    signatory_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    signatory_relationship = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    risks_explained_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    risks_explained_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    witness_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    witness_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    signed_form_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    searched_by = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    police_informed = table.Column<bool>(type: "boolean", nullable: false),
                    police_station = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    police_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    relatives_informed = table.Column<bool>(type: "boolean", nullable: false),
                    relative_informed = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    death_certificate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    death_certificate_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    certificate_pending_post_mortem = table.Column<bool>(type: "boolean", nullable: false),
                    dues_outstanding = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_discharge_declarations", x => x.id);
                    table.ForeignKey(
                        name: "fk_discharge_declarations_admissions_admission_id",
                        column: x => x.admission_id,
                        principalSchema: "inpatient",
                        principalTable: "admissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discharge_declarations_admission_id",
                schema: "inpatient",
                table: "discharge_declarations",
                column: "admission_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_discharge_declarations_facility_id",
                schema: "inpatient",
                table: "discharge_declarations",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_discharge_declarations_is_deleted",
                schema: "inpatient",
                table: "discharge_declarations",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discharge_declarations",
                schema: "inpatient");

            migrationBuilder.DropColumn(
                name: "lama_doctor_name",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropColumn(
                name: "lama_doctor_user_id",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropColumn(
                name: "lama_relationship",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropColumn(
                name: "lama_signatory_name",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropColumn(
                name: "lama_signed_by",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropColumn(
                name: "lama_signed_form_file_id",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropColumn(
                name: "lama_witness_name",
                schema: "clinical",
                table: "emergency_cases");

            migrationBuilder.DropColumn(
                name: "lama_witness_user_id",
                schema: "clinical",
                table: "emergency_cases");
        }
    }
}
