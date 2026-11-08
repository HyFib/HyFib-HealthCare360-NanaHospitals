using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StatutoryReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "tds_basis",
                schema: "billing",
                table: "doctor_payouts",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "tds_pan_cipher",
                schema: "billing",
                table: "doctor_payouts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tds_remark",
                schema: "billing",
                table: "doctor_payouts",
                type: "character varying(1)",
                maxLength: 1,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "doctor_tax_profiles",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pan_cipher = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    pan_masked = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    name_as_per_pan = table.Column<string>(type: "character varying(75)", maxLength: 75, nullable: true),
                    certificate_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    certificate_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    certificate_valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    certificate_valid_to = table.Column<DateOnly>(type: "date", nullable: true),
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
                    table.PrimaryKey("pk_doctor_tax_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "statutory_employer_profiles",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tan = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    pan = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    deductor_name = table.Column<string>(type: "character varying(75)", maxLength: 75, nullable: true),
                    branch = table.Column<string>(type: "character varying(75)", maxLength: 75, nullable: true),
                    deductor_type = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    address_line1 = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    address_line2 = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    address_line3 = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    city = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    state_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    pin = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true),
                    email = table.Column<string>(type: "character varying(75)", maxLength: 75, nullable: true),
                    phone = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    responsible_name = table.Column<string>(type: "character varying(75)", maxLength: 75, nullable: true),
                    responsible_designation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    responsible_pan = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    responsible_mobile = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    responsible_email = table.Column<string>(type: "character varying(75)", maxLength: 75, nullable: true),
                    epf_establishment_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    esic_employer_code = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    salary_debit_account = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: true),
                    salary_bank = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
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
                    table.PrimaryKey("pk_statutory_employer_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_declarations",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    financial_year = table.Column<int>(type: "integer", nullable: false),
                    regime = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rent_paid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    metro_city = table.Column<bool>(type: "boolean", nullable: false),
                    section80c = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    section80ccd1b = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    section80d_self = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    section80d_parents = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    parents_senior = table.Column<bool>(type: "boolean", nullable: false),
                    section80e = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    section80g = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    section80tta = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    housing_loan_interest = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    other_income = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    previous_employer_salary = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    previous_employer_tds = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_tax_declarations", x => x.id);
                    table.ForeignKey(
                        name: "fk_tax_declarations_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "hr",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tds_challans",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    bsr_code = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    deposited_on = table.Column<DateOnly>(type: "date", nullable: false),
                    challan_serial = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    surcharge = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cess = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    interest = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    others = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    late_fee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    minor_head = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
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
                    table.PrimaryKey("pk_tds_challans", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_doctor_tax_profiles_is_deleted",
                schema: "billing",
                table: "doctor_tax_profiles",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_doctor_tax_profiles_practitioner_id",
                schema: "billing",
                table: "doctor_tax_profiles",
                column: "practitioner_id",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_statutory_employer_profiles_facility_id",
                schema: "hr",
                table: "statutory_employer_profiles",
                column: "facility_id",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_statutory_employer_profiles_is_deleted",
                schema: "hr",
                table: "statutory_employer_profiles",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_tax_declarations_employee_id_financial_year",
                schema: "hr",
                table: "tax_declarations",
                columns: new[] { "employee_id", "financial_year" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_tax_declarations_facility_id",
                schema: "hr",
                table: "tax_declarations",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_tax_declarations_is_deleted",
                schema: "hr",
                table: "tax_declarations",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_tds_challans_facility_id",
                schema: "hr",
                table: "tds_challans",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_tds_challans_facility_id_bsr_code_deposited_on_challan_seri",
                schema: "hr",
                table: "tds_challans",
                columns: new[] { "facility_id", "bsr_code", "deposited_on", "challan_serial" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_tds_challans_facility_id_form_year_month",
                schema: "hr",
                table: "tds_challans",
                columns: new[] { "facility_id", "form", "year", "month" });

            migrationBuilder.CreateIndex(
                name: "ix_tds_challans_is_deleted",
                schema: "hr",
                table: "tds_challans",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "doctor_tax_profiles",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "statutory_employer_profiles",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "tax_declarations",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "tds_challans",
                schema: "hr");

            migrationBuilder.DropColumn(
                name: "tds_basis",
                schema: "billing",
                table: "doctor_payouts");

            migrationBuilder.DropColumn(
                name: "tds_pan_cipher",
                schema: "billing",
                table: "doctor_payouts");

            migrationBuilder.DropColumn(
                name: "tds_remark",
                schema: "billing",
                table: "doctor_payouts");
        }
    }
}
