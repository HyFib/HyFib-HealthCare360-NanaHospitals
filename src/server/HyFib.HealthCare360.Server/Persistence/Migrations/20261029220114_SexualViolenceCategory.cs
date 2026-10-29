using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SexualViolenceCategory : Migration
    {
        /// <summary>
        /// Records already kept: a sexual assault in the MLC register, or a diagnosis of sexual abuse (T74.2, T76.2),
        /// sexual assault (Y05) or an examination after an alleged rape (Z04.4), puts the patient's record in the
        /// sexual-violence category (8), as they do from now on (EmergencyService.SaveMlcAsync, SensitiveCategories.FromIcd).
        /// </summary>
        public const string MarkVictimsRecords = """
            UPDATE patients.patients AS p
            SET sensitive_categories = p.sensitive_categories | 8
            WHERE EXISTS (
                    SELECT 1 FROM clinical.mlc_records AS m
                    WHERE m.patient_id = p.id AND m.cause = 'SexualAssault' AND NOT m.is_deleted)
               OR EXISTS (
                    SELECT 1 FROM clinical.diagnoses AS d
                    WHERE d.patient_id = p.id AND NOT d.is_deleted AND d.icd_code IS NOT NULL
                      AND (left(upper(btrim(d.icd_code)), 3) = 'Y05'
                           OR upper(replace(btrim(d.icd_code), '.', '')) LIKE ANY (ARRAY['T742%', 'T762%', 'Z044%'])));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(MarkVictimsRecords);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The records stay confidential: a doctor takes a record out of the category, with a reason, if it was put in by mistake.
        }
    }
}
