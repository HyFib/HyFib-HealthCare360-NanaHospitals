using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InteropPlatformGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_devices",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    identifier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_cipher = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    refused_attempts = table.Column<int>(type: "integer", nullable: false),
                    added_by = table.Column<Guid>(type: "uuid", nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_lab_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "practitioner_hpr_checks",
                schema: "org",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_by = table.Column<Guid>(type: "uuid", nullable: false),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    mismatches = table.Column<List<string>>(type: "text[]", nullable: false),
                    checked_values = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    registry_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    registry_council = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    registry_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practitioner_hpr_checks", x => x.id);
                    table.ForeignKey(
                        name: "fk_practitioner_hpr_checks_practitioners_practitioner_id",
                        column: x => x.practitioner_id,
                        principalSchema: "org",
                        principalTable: "practitioners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "smart_clients",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    redirect_uris_json = table.Column<string>(type: "jsonb", nullable: false),
                    jwks_json = table.Column<string>(type: "jsonb", nullable: true),
                    scopes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    service_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: true),
                    replaces_password_sign_in = table.Column<bool>(type: "boolean", nullable: false),
                    registered_by = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    suspended_by = table.Column<Guid>(type: "uuid", nullable: true),
                    suspended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    suspended_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_smart_clients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "smart_access_tokens",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    smart_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    grant_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scopes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_smart_access_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_smart_access_tokens_smart_client_smart_client_id",
                        column: x => x.smart_client_id,
                        principalSchema: "interop",
                        principalTable: "smart_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "smart_assertion_uses",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    smart_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jti = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_smart_assertion_uses", x => x.id);
                    table.ForeignKey(
                        name: "fk_smart_assertion_uses_smart_client_smart_client_id",
                        column: x => x.smart_client_id,
                        principalSchema: "interop",
                        principalTable: "smart_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "smart_authorization_codes",
                schema: "interop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    smart_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    redirect_uri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    code_challenge = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scopes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    launch_patient = table.Column<bool>(type: "boolean", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    token_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_smart_authorization_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_smart_authorization_codes_smart_client_smart_client_id",
                        column: x => x.smart_client_id,
                        principalSchema: "interop",
                        principalTable: "smart_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lab_devices_connection_code_method_identifier",
                schema: "integrations",
                table: "lab_devices",
                columns: new[] { "connection_code", "method", "identifier" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_lab_devices_is_deleted",
                schema: "integrations",
                table: "lab_devices",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_practitioner_hpr_checks_practitioner_id_checked_at",
                schema: "org",
                table: "practitioner_hpr_checks",
                columns: new[] { "practitioner_id", "checked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_smart_access_tokens_smart_client_id_expires_at",
                schema: "interop",
                table: "smart_access_tokens",
                columns: new[] { "smart_client_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_smart_access_tokens_token_hash",
                schema: "interop",
                table: "smart_access_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_smart_assertion_uses_smart_client_id_jti",
                schema: "interop",
                table: "smart_assertion_uses",
                columns: new[] { "smart_client_id", "jti" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_smart_authorization_codes_code_hash",
                schema: "interop",
                table: "smart_authorization_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_smart_authorization_codes_smart_client_id",
                schema: "interop",
                table: "smart_authorization_codes",
                column: "smart_client_id");

            migrationBuilder.CreateIndex(
                name: "ix_smart_clients_client_id",
                schema: "interop",
                table: "smart_clients",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_smart_clients_is_deleted",
                schema: "interop",
                table: "smart_clients",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_smart_clients_service_user_id",
                schema: "interop",
                table: "smart_clients",
                column: "service_user_id");

            // Visitor passes always carry the facility code: a format saved before the rule gets it in front, as the
            // numbers made since already do (NumberSeriesService.WithFacilityCode).
            migrationBuilder.Sql("""
                UPDATE platform.number_series SET format = '{FAC}' || format
                 WHERE key = 'visitor-pass' AND position('{FAC}' in format) = 0
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_devices",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "practitioner_hpr_checks",
                schema: "org");

            migrationBuilder.DropTable(
                name: "smart_access_tokens",
                schema: "interop");

            migrationBuilder.DropTable(
                name: "smart_assertion_uses",
                schema: "interop");

            migrationBuilder.DropTable(
                name: "smart_authorization_codes",
                schema: "interop");

            migrationBuilder.DropTable(
                name: "smart_clients",
                schema: "interop");
        }
    }
}
