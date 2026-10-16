using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WebAuthnUnlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "options",
                schema: "identity",
                table: "auth_challenges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                schema: "identity",
                table: "auth_challenges",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "webauthn_credentials",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<byte[]>(type: "bytea", maxLength: 1023, nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", maxLength: 2048, nullable: false),
                    sign_count = table.Column<long>(type: "bigint", nullable: false),
                    relying_party_id = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aa_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    transports = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    is_backup_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webauthn_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_webauthn_credentials_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_challenges_session_id",
                schema: "identity",
                table: "auth_challenges",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_webauthn_credentials_credential_id",
                schema: "identity",
                table: "webauthn_credentials",
                column: "credential_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webauthn_credentials_user_id",
                schema: "identity",
                table: "webauthn_credentials",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webauthn_credentials",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "ix_auth_challenges_session_id",
                schema: "identity",
                table: "auth_challenges");

            migrationBuilder.DropColumn(
                name: "options",
                schema: "identity",
                table: "auth_challenges");

            migrationBuilder.DropColumn(
                name: "session_id",
                schema: "identity",
                table: "auth_challenges");
        }
    }
}
