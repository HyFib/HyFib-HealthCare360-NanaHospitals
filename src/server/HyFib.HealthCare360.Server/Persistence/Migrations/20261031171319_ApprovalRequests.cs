using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <summary>
    /// Maker-checker: changes one person asks for and another approves (<c>platform.approval_requests</c>), each with the
    /// change as asked, its SHA-256 and the version of the record it was asked against. A credit note keeps who asked for
    /// it beside who approved and issued it; notes issued before this have no one in <c>requested_by</c>.
    /// </summary>
    public partial class ApprovalRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "requested_by",
                schema: "billing",
                table: "credit_notes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_by_name",
                schema: "billing",
                table: "credit_notes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "approval_requests",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_version = table.Column<long>(type: "bigint", nullable: true),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("pk_approval_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_facility_id",
                schema: "platform",
                table: "approval_requests",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_facility_id_status_kind",
                schema: "platform",
                table: "approval_requests",
                columns: new[] { "facility_id", "status", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_is_deleted",
                schema: "platform",
                table: "approval_requests",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_requested_by",
                schema: "platform",
                table: "approval_requests",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_subject_type_subject_id",
                schema: "platform",
                table: "approval_requests",
                columns: new[] { "subject_type", "subject_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_requests",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "requested_by",
                schema: "billing",
                table: "credit_notes");

            migrationBuilder.DropColumn(
                name: "requested_by_name",
                schema: "billing",
                table: "credit_notes");
        }
    }
}
