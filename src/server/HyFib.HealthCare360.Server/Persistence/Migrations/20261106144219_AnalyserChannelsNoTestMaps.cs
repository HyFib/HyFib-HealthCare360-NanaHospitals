using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnalyserChannelsNoTestMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_analyser_channels",
                schema: "diagnostics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    times_seen = table.Column<int>(type: "integer", nullable: false),
                    last_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lab_analyser_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_lab_analyser_channels_lab_instrument_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "diagnostics",
                        principalTable: "lab_instruments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lab_ignored_analyser_channels",
                schema: "diagnostics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
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
                    table.PrimaryKey("pk_lab_ignored_analyser_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_lab_ignored_analyser_channels_lab_instrument_instrument_id",
                        column: x => x.instrument_id,
                        principalSchema: "diagnostics",
                        principalTable: "lab_instruments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lab_analyser_channels_facility_id",
                schema: "diagnostics",
                table: "lab_analyser_channels",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_lab_analyser_channels_instrument_id_channel_code",
                schema: "diagnostics",
                table: "lab_analyser_channels",
                columns: new[] { "instrument_id", "channel_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lab_ignored_analyser_channels_facility_id",
                schema: "diagnostics",
                table: "lab_ignored_analyser_channels",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_lab_ignored_analyser_channels_instrument_id_channel_code",
                schema: "diagnostics",
                table: "lab_ignored_analyser_channels",
                columns: new[] { "instrument_id", "channel_code" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_lab_ignored_analyser_channels_is_deleted",
                schema: "diagnostics",
                table: "lab_ignored_analyser_channels",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_analyser_channels",
                schema: "diagnostics");

            migrationBuilder.DropTable(
                name: "lab_ignored_analyser_channels",
                schema: "diagnostics");
        }
    }
}
