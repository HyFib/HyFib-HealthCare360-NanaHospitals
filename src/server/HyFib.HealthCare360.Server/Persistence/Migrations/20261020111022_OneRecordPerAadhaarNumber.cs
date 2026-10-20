using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneRecordPerAadhaarNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_patients_aadhaar_index",
                schema: "patients",
                table: "patients");

            // Registration looked for the number and then saved, so two desks could link one number to two records at the
            // same moment. Should a database hold such a pair, the record registered first keeps the link and the other
            // keeps only the last four digits, so the index can be made without stopping the upgrade. An identity check on
            // that record then names the record that has the number, for the two to be merged.
            migrationBuilder.Sql("""
                UPDATE patients.patients AS later SET aadhaar_index = NULL
                WHERE later.aadhaar_index IS NOT NULL AND NOT later.is_deleted
                  AND EXISTS (
                      SELECT 1 FROM patients.patients AS earlier
                      WHERE earlier.aadhaar_index = later.aadhaar_index AND NOT earlier.is_deleted
                        AND (earlier.registered_at, earlier.id) < (later.registered_at, later.id));
                """);

            migrationBuilder.CreateIndex(
                name: "ux_patients_aadhaar_index",
                schema: "patients",
                table: "patients",
                column: "aadhaar_index",
                unique: true,
                filter: "aadhaar_index IS NOT NULL AND is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_patients_aadhaar_index",
                schema: "patients",
                table: "patients");

            migrationBuilder.CreateIndex(
                name: "ix_patients_aadhaar_index",
                schema: "patients",
                table: "patients",
                column: "aadhaar_index");
        }
    }
}
