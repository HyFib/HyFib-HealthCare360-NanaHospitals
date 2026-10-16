using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WhatsAppOptIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "whatsapp_opt_ins",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    opted_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    captured_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
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
                    table.PrimaryKey("pk_whatsapp_opt_ins", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_opt_ins_is_deleted",
                schema: "platform",
                table: "whatsapp_opt_ins",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_opt_ins_patient_id",
                schema: "platform",
                table: "whatsapp_opt_ins",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_opt_ins_phone_opted_in_at",
                schema: "platform",
                table: "whatsapp_opt_ins",
                columns: new[] { "phone", "opted_in_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whatsapp_opt_ins",
                schema: "platform");
        }
    }
}
