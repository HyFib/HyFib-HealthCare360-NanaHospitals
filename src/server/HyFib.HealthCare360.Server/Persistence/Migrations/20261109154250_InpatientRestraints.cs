using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InpatientRestraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "restraint_episodes",
                schema: "inpatient",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ward_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    method = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    alternatives_tried = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    check_interval_minutes = table.Column<int>(type: "integer", nullable: false),
                    ordered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ordered_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ordered_by_practitioner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ordered_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ordered_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    renewals = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    released_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    release_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    debrief = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    debriefed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    debriefed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_restraint_episodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_restraint_episodes_admissions_admission_id",
                        column: x => x.admission_id,
                        principalSchema: "inpatient",
                        principalTable: "admissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restraint_checks",
                schema: "inpatient",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    circulation_ok = table.Column<bool>(type: "boolean", nullable: false),
                    skin_intact = table.Column<bool>(type: "boolean", nullable: false),
                    fluids_offered = table.Column<bool>(type: "boolean", nullable: false),
                    toileting_offered = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    checked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_restraint_checks", x => x.id);
                    table.ForeignKey(
                        name: "fk_restraint_checks_restraint_episode_episode_id",
                        column: x => x.episode_id,
                        principalSchema: "inpatient",
                        principalTable: "restraint_episodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_restraint_checks_episode_id_checked_at",
                schema: "inpatient",
                table: "restraint_checks",
                columns: new[] { "episode_id", "checked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_restraint_checks_facility_id",
                schema: "inpatient",
                table: "restraint_checks",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_restraint_checks_is_deleted",
                schema: "inpatient",
                table: "restraint_checks",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_restraint_checks_patient_id",
                schema: "inpatient",
                table: "restraint_checks",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_restraint_episodes_admission_id_ordered_at",
                schema: "inpatient",
                table: "restraint_episodes",
                columns: new[] { "admission_id", "ordered_at" });

            migrationBuilder.CreateIndex(
                name: "ix_restraint_episodes_facility_id",
                schema: "inpatient",
                table: "restraint_episodes",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_restraint_episodes_facility_id_ward_id_status",
                schema: "inpatient",
                table: "restraint_episodes",
                columns: new[] { "facility_id", "ward_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_restraint_episodes_is_deleted",
                schema: "inpatient",
                table: "restraint_episodes",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_restraint_episodes_patient_id",
                schema: "inpatient",
                table: "restraint_episodes",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "restraint_checks",
                schema: "inpatient");

            migrationBuilder.DropTable(
                name: "restraint_episodes",
                schema: "inpatient");
        }
    }
}
