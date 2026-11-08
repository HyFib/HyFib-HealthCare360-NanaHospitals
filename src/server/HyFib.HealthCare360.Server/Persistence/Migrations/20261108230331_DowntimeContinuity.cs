using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DowntimeContinuity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "downtime_number_blocks",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    counter = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    first_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    last_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    issued_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reset_policy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    period = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    returned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    returned_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    return_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
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
                    table.PrimaryKey("pk_downtime_number_blocks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "downtime_pack_snapshots",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generated_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    patients = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("pk_downtime_pack_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "late_back_entry_grants",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    paper_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paper_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    max_records = table.Column<int>(type: "integer", nullable: false),
                    used = table.Column<int>(type: "integer", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("pk_late_back_entry_grants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "downtime_numbers",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    block_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paper_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    entered_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    late_grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    voided_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_downtime_numbers", x => x.id);
                    table.ForeignKey(
                        name: "fk_downtime_numbers_downtime_number_blocks_block_id",
                        column: x => x.block_id,
                        principalSchema: "patients",
                        principalTable: "downtime_number_blocks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_downtime_number_blocks_facility_id_kind_created_at",
                schema: "patients",
                table: "downtime_number_blocks",
                columns: new[] { "facility_id", "kind", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_downtime_number_blocks_is_deleted",
                schema: "patients",
                table: "downtime_number_blocks",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_downtime_numbers_block_id_number",
                schema: "patients",
                table: "downtime_numbers",
                columns: new[] { "block_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_downtime_numbers_block_id_position",
                schema: "patients",
                table: "downtime_numbers",
                columns: new[] { "block_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_downtime_numbers_entity_type_entity_id",
                schema: "patients",
                table: "downtime_numbers",
                columns: new[] { "entity_type", "entity_id" },
                unique: true,
                filter: "entity_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_downtime_numbers_is_deleted",
                schema: "patients",
                table: "downtime_numbers",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_downtime_numbers_number",
                schema: "patients",
                table: "downtime_numbers",
                column: "number");

            migrationBuilder.CreateIndex(
                name: "ix_downtime_pack_snapshots_facility_id_generated_at",
                schema: "patients",
                table: "downtime_pack_snapshots",
                columns: new[] { "facility_id", "generated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_downtime_pack_snapshots_is_deleted",
                schema: "patients",
                table: "downtime_pack_snapshots",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_late_back_entry_grants_approval_request_id",
                schema: "patients",
                table: "late_back_entry_grants",
                column: "approval_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_late_back_entry_grants_facility_id_requested_by_record_expi",
                schema: "patients",
                table: "late_back_entry_grants",
                columns: new[] { "facility_id", "requested_by", "record", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_late_back_entry_grants_is_deleted",
                schema: "patients",
                table: "late_back_entry_grants",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "downtime_numbers",
                schema: "patients");

            migrationBuilder.DropTable(
                name: "downtime_pack_snapshots",
                schema: "patients");

            migrationBuilder.DropTable(
                name: "late_back_entry_grants",
                schema: "patients");

            migrationBuilder.DropTable(
                name: "downtime_number_blocks",
                schema: "patients");
        }
    }
}
