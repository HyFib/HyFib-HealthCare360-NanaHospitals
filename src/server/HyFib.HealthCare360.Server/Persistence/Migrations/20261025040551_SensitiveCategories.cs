using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SensitiveCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "sensitive_categories",
                schema: "patients",
                table: "patients",
                type: "integer",
                maxLength: 64,
                nullable: false,
                defaultValue: 0);

            // Records already carrying an HIV, mental-illness or medical-abortion diagnosis go into its category now, as a
            // new diagnosis puts them (SensitiveCategories.FromIcd): Hiv = 1, MentalHealth = 2, Mtp = 4.
            migrationBuilder.Sql("""
                UPDATE patients.patients AS p
                SET sensitive_categories = p.sensitive_categories | d.categories
                FROM (
                    SELECT patient_id, bit_or(CASE
                        WHEN left(upper(btrim(icd_code)), 3) IN ('B20', 'B21', 'B22', 'B23', 'B24', 'Z21') THEN 1
                        WHEN left(upper(btrim(icd_code)), 3) ~ '^F[2-6][0-9]$' THEN 2
                        WHEN left(upper(btrim(icd_code)), 3) = 'O04' THEN 4
                        ELSE 0 END) AS categories
                    FROM clinical.diagnoses
                    WHERE NOT is_deleted AND icd_code IS NOT NULL
                    GROUP BY patient_id
                ) AS d
                WHERE d.patient_id = p.id AND d.categories <> 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sensitive_categories",
                schema: "patients",
                table: "patients");
        }
    }
}
