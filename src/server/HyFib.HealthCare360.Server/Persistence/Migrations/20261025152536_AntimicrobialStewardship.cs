using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AntimicrobialStewardship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "antimicrobial_approvals",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prescription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prescription_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prescription_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    drug_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    antimicrobial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    dose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    route = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    duration_days = table.Column<int>(type: "integer", nullable: false),
                    prescribed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prescribed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    prescribed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    site = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    indication = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    empirical = table.Column<bool>(type: "boolean", nullable: true),
                    culture_sent = table.Column<bool>(type: "boolean", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    decided_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_days = table.Column<int>(type: "integer", nullable: true),
                    decision_notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    culture_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    organism = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    susceptibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    culture_released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_decision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reviewed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_antimicrobial_approvals", x => x.id);
                    table.ForeignKey(
                        name: "fk_antimicrobial_approvals_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_antimicrobial_approvals_facility_id",
                schema: "clinical",
                table: "antimicrobial_approvals",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_antimicrobial_approvals_facility_id_status",
                schema: "clinical",
                table: "antimicrobial_approvals",
                columns: new[] { "facility_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_antimicrobial_approvals_is_deleted",
                schema: "clinical",
                table: "antimicrobial_approvals",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_antimicrobial_approvals_patient_id",
                schema: "clinical",
                table: "antimicrobial_approvals",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_antimicrobial_approvals_prescription_line_id",
                schema: "clinical",
                table: "antimicrobial_approvals",
                column: "prescription_line_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "antimicrobial_approvals",
                schema: "clinical");
        }
    }
}
