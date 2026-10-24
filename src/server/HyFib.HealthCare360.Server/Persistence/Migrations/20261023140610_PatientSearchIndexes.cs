using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PatientSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_patients_alternate_phone",
                schema: "patients",
                table: "patients",
                column: "alternate_phone");

            migrationBuilder.CreateIndex(
                name: "ix_patients_phone_trigram",
                schema: "patients",
                table: "patients",
                column: "phone")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_patients_uhid_prefix",
                schema: "patients",
                table: "patients",
                column: "uhid")
                .Annotation("Npgsql:IndexOperators", new[] { "varchar_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_patients_uhid_trigram",
                schema: "patients",
                table: "patients",
                column: "uhid")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_patients_alternate_phone",
                schema: "patients",
                table: "patients");

            migrationBuilder.DropIndex(
                name: "ix_patients_phone_trigram",
                schema: "patients",
                table: "patients");

            migrationBuilder.DropIndex(
                name: "ix_patients_uhid_prefix",
                schema: "patients",
                table: "patients");

            migrationBuilder.DropIndex(
                name: "ix_patients_uhid_trigram",
                schema: "patients",
                table: "patients");
        }
    }
}
