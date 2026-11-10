using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppointmentReplacedOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "appointment_replaced_orders",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    replaced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    gateway_payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    amount_paid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appointment_replaced_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_appointment_replaced_orders_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalSchema: "scheduling",
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_appointment_replaced_orders_appointment_id",
                schema: "scheduling",
                table: "appointment_replaced_orders",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointment_replaced_orders_facility_id",
                schema: "scheduling",
                table: "appointment_replaced_orders",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointment_replaced_orders_gateway_payment_id",
                schema: "scheduling",
                table: "appointment_replaced_orders",
                column: "gateway_payment_id",
                filter: "gateway_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_appointment_replaced_orders_order_id",
                schema: "scheduling",
                table: "appointment_replaced_orders",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_appointment_replaced_orders_replaced_at",
                schema: "scheduling",
                table: "appointment_replaced_orders",
                column: "replaced_at",
                filter: "gateway_payment_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointment_replaced_orders",
                schema: "scheduling");
        }
    }
}
