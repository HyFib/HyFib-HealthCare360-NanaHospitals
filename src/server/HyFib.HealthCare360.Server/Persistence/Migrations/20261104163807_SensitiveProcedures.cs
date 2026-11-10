using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SensitiveProcedures : Migration
    {
        /// <summary>
        /// Records already kept: a procedure that ends a pregnancy (ICD-10-PCS root operation Abortion, 10A) puts the
        /// patient's record in the MTP category (4), and electroconvulsive therapy (GZB) in the mental health category (2),
        /// as they do from now on when recorded, corrected or completed in theatre (ProcedureService,
        /// SensitiveCategories.FromIcd10Pcs). A procedure marked as entered in error is known to be wrong, and is left out.
        /// </summary>
        public const string MarkRecordsOfSensitiveProcedures = """
            UPDATE patients.patients AS p
            SET sensitive_categories = p.sensitive_categories | r.categories
            FROM (
                SELECT patient_id, bit_or(CASE
                    WHEN left(upper(btrim(code)), 3) = '10A' THEN 4
                    WHEN left(upper(btrim(code)), 3) = 'GZB' THEN 2
                    ELSE 0 END) AS categories
                FROM clinical.procedure_records
                WHERE code_system = 'ICD-10-PCS' AND length(btrim(code)) = 7 AND status <> 'EnteredInError'
                GROUP BY patient_id
            ) AS r
            WHERE r.patient_id = p.id AND r.categories <> 0;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(MarkRecordsOfSensitiveProcedures);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The records stay confidential: a doctor takes a record out of a category, with a reason, if it was put in by mistake.
        }
    }
}
