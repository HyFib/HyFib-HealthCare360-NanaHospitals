using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PfWagesUnderTheLabourCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every pay head counts as wages unless it is one the Code on Social Security 2020 leaves out (s.2(88)): of the
            // seeded ones, house rent, conveyance and overtime. Pay heads a hospital added itself are wages until payroll
            // marks them otherwise.
            migrationBuilder.AddColumn<string>(
                name: "wage_treatment",
                schema: "hr",
                table: "salary_components",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Wages");

            migrationBuilder.Sql("UPDATE hr.salary_components SET wage_treatment = 'Excluded' WHERE code IN ('HRA', 'CONV', 'OT');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "wage_treatment",
                schema: "hr",
                table: "salary_components");
        }
    }
}
