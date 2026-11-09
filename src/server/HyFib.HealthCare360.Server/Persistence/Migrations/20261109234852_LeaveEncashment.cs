using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeaveEncashment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "in_service_encashment_days_per_year",
                schema: "hr",
                table: "leave_types",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "minimum_balance_after_encashment",
                schema: "hr",
                table: "leave_types",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "leave_encashments",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    days = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    daily_rate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payroll_input_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_working_day = table.Column<DateOnly>(type: "date", nullable: true),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
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
                    table.PrimaryKey("pk_leave_encashments", x => x.id);
                    table.ForeignKey(
                        name: "fk_leave_encashments_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "hr",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_leave_encashments_leave_type_leave_type_id",
                        column: x => x.leave_type_id,
                        principalSchema: "hr",
                        principalTable: "leave_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_leave_encashments_approval_id",
                schema: "hr",
                table: "leave_encashments",
                column: "approval_id",
                unique: true,
                filter: "approval_id IS NOT NULL AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_leave_encashments_employee_id_leave_type_id_year",
                schema: "hr",
                table: "leave_encashments",
                columns: new[] { "employee_id", "leave_type_id", "year" });

            migrationBuilder.CreateIndex(
                name: "ix_leave_encashments_facility_id",
                schema: "hr",
                table: "leave_encashments",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_leave_encashments_is_deleted",
                schema: "hr",
                table: "leave_encashments",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_leave_encashments_leave_type_id",
                schema: "hr",
                table: "leave_encashments",
                column: "leave_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_leave_encashments_payroll_input_id",
                schema: "hr",
                table: "leave_encashments",
                column: "payroll_input_id",
                unique: true,
                filter: "payroll_input_id IS NOT NULL AND is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "leave_encashments",
                schema: "hr");

            migrationBuilder.DropColumn(
                name: "in_service_encashment_days_per_year",
                schema: "hr",
                table: "leave_types");

            migrationBuilder.DropColumn(
                name: "minimum_balance_after_encashment",
                schema: "hr",
                table: "leave_types");
        }
    }
}
