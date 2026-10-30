using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FreeTreatment : Migration
    {
        /// <summary>
        /// Cases already in the register: the victim of a sexual assault (or an acid attack) is treated free of cost (BNSS
        /// 2023, s.397), so the case's visit, the stays the patient was admitted to from it and the visits linked to its MLC
        /// number are marked, as they are from now on (Clinical's FreeTreatment). Bills already finalized stay as issued; a
        /// draft comes to nothing when it is next refreshed or finalized.
        /// </summary>
        public const string MarkVictimsVisits = """
            UPDATE patients.visits AS v
            SET free_treatment_reason = 'BNSS 2023 s.397, MLC no. ' || m.number
            FROM clinical.mlc_records AS m
            WHERE m.cause IN ('SexualAssault', 'AcidAttack') AND NOT m.is_deleted
              AND v.patient_id = m.patient_id AND NOT v.is_deleted AND v.free_treatment_reason IS NULL
              AND (v.id = m.visit_id
                   OR (v.type = 'InPatient' AND v.previous_visit_id = m.visit_id)
                   OR (v.is_mlc AND upper(btrim(v.mlc_number)) = upper(m.number)));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "free_treatment_reason",
                schema: "patients",
                table: "visits",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "free_treatment_reason",
                schema: "pharmacy",
                table: "sales",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "free_treatment_amount",
                schema: "billing",
                table: "bills",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "free_treatment_reason",
                schema: "billing",
                table: "bills",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.Sql(MarkVictimsVisits);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "free_treatment_reason",
                schema: "patients",
                table: "visits");

            migrationBuilder.DropColumn(
                name: "free_treatment_reason",
                schema: "pharmacy",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "free_treatment_amount",
                schema: "billing",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "free_treatment_reason",
                schema: "billing",
                table: "bills");
        }
    }
}
