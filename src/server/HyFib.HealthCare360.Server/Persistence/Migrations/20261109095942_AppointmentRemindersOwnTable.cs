using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppointmentRemindersOwnTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "appointment_reminders",
                schema: "scheduling",
                columns: table => new
                {
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appointment_reminders", x => new { x.appointment_id, x.kind });
                    table.ForeignKey(
                        name: "fk_appointment_reminders_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalSchema: "scheduling",
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The stamps the appointments carry move to the new table before their columns go.
            migrationBuilder.Sql("""
                INSERT INTO scheduling.appointment_reminders (appointment_id, kind, sent_at)
                SELECT id, 'DayBefore', reminder24h_sent_at FROM scheduling.appointments WHERE reminder24h_sent_at IS NOT NULL
                UNION ALL
                SELECT id, 'TwoHoursBefore', reminder2h_sent_at FROM scheduling.appointments WHERE reminder2h_sent_at IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "reminder24h_sent_at",
                schema: "scheduling",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "reminder2h_sent_at",
                schema: "scheduling",
                table: "appointments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reminder24h_sent_at",
                schema: "scheduling",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reminder2h_sent_at",
                schema: "scheduling",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE scheduling.appointments a SET reminder24h_sent_at = r.sent_at
                FROM scheduling.appointment_reminders r WHERE r.appointment_id = a.id AND r.kind = 'DayBefore';
                UPDATE scheduling.appointments a SET reminder2h_sent_at = r.sent_at
                FROM scheduling.appointment_reminders r WHERE r.appointment_id = a.id AND r.kind = 'TwoHoursBefore';
                """);

            migrationBuilder.DropTable(
                name: "appointment_reminders",
                schema: "scheduling");
        }
    }
}
