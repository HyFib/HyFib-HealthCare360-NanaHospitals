using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DowntimeUhids : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "downtime_uhid_blocks",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    first_uhid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_uhid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    issued_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_downtime_uhid_blocks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "downtime_uhids",
                schema: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    block_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    uhid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paper_registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    entered_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("pk_downtime_uhids", x => x.id);
                    table.ForeignKey(
                        name: "fk_downtime_uhids_downtime_uhid_blocks_block_id",
                        column: x => x.block_id,
                        principalSchema: "patients",
                        principalTable: "downtime_uhid_blocks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_downtime_uhids_patient_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "patients",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_downtime_uhid_blocks_facility_id_created_at",
                schema: "patients",
                table: "downtime_uhid_blocks",
                columns: new[] { "facility_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_downtime_uhid_blocks_is_deleted",
                schema: "patients",
                table: "downtime_uhid_blocks",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_downtime_uhids_block_id_position",
                schema: "patients",
                table: "downtime_uhids",
                columns: new[] { "block_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_downtime_uhids_is_deleted",
                schema: "patients",
                table: "downtime_uhids",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_downtime_uhids_patient_id",
                schema: "patients",
                table: "downtime_uhids",
                column: "patient_id",
                unique: true,
                filter: "patient_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_downtime_uhids_uhid",
                schema: "patients",
                table: "downtime_uhids",
                column: "uhid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "downtime_uhids",
                schema: "patients");

            migrationBuilder.DropTable(
                name: "downtime_uhid_blocks",
                schema: "patients");
        }
    }
}
