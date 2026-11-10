using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImmunizationFacility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "facility_id",
                schema: "clinical",
                table: "immunizations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // A dose recorded before this was kept is filed under the facility of the encounter it was given in, else the
            // facility the patient was registered at: the custodian of its ABDM immunisation record.
            migrationBuilder.Sql("""
                UPDATE clinical.immunizations i
                SET facility_id = COALESCE(
                    (SELECT e.facility_id FROM clinical.encounters e WHERE e.id = i.encounter_id),
                    (SELECT p.registered_facility_id FROM patients.patients p WHERE p.id = i.patient_id))
                WHERE i.facility_id = '00000000-0000-0000-0000-000000000000';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "facility_id",
                schema: "clinical",
                table: "immunizations");
        }
    }
}
