using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CardiologyPacsObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "study_instance_uid",
                schema: "diagnostics",
                table: "cardiology_studies",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cardiology_pacs_objects",
                schema: "diagnostics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cardiology_study_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sop_instance_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    destination = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("pk_cardiology_pacs_objects", x => x.id);
                    table.ForeignKey(
                        name: "fk_cardiology_pacs_objects_cardiology_study_cardiology_study_id",
                        column: x => x.cardiology_study_id,
                        principalSchema: "diagnostics",
                        principalTable: "cardiology_studies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cardiology_pacs_objects_cardiology_study_id_file_id",
                schema: "diagnostics",
                table: "cardiology_pacs_objects",
                columns: new[] { "cardiology_study_id", "file_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cardiology_pacs_objects_facility_id",
                schema: "diagnostics",
                table: "cardiology_pacs_objects",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_cardiology_pacs_objects_is_deleted",
                schema: "diagnostics",
                table: "cardiology_pacs_objects",
                column: "is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cardiology_pacs_objects",
                schema: "diagnostics");

            migrationBuilder.DropColumn(
                name: "study_instance_uid",
                schema: "diagnostics",
                table: "cardiology_studies");
        }
    }
}
