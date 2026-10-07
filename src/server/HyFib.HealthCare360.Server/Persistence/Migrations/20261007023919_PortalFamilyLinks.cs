using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PortalFamilyLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portal_family_codes",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    relation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portal_family_codes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "portal_family_links",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    linked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consent_given_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    code_sent_to = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
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
                    table.PrimaryKey("pk_portal_family_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_portal_family_links_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_portal_family_codes_account_phone_created_at",
                schema: "patients",
                table: "portal_family_codes",
                columns: new[] { "account_phone", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_portal_family_codes_target_phone_created_at",
                schema: "patients",
                table: "portal_family_codes",
                columns: new[] { "target_phone", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_portal_family_links_account_phone_patient_id",
                schema: "patients",
                table: "portal_family_links",
                columns: new[] { "account_phone", "patient_id" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_portal_family_links_is_deleted",
                schema: "patients",
                table: "portal_family_links",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_portal_family_links_patient_id",
                schema: "patients",
                table: "portal_family_links",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portal_family_codes",
                schema: "patients");

            migrationBuilder.DropTable(
                name: "portal_family_links",
                schema: "patients");
        }
    }
}
