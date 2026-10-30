using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <summary>
    /// The files of a copy of the record issued as documents (BSA 2023, s.63): each document as it was handed over, with its
    /// SHA-256. The database refuses to change or delete one, as it does a controlled-drug register line: what was issued
    /// stays as it was issued.
    /// </summary>
    public partial class RecordCopies : Migration
    {
        internal const string Guard = """
            CREATE OR REPLACE FUNCTION clinical.protect_record_copy_files() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION '%.% is append-only: a file of a copy issued is never changed or deleted', TG_TABLE_SCHEMA, TG_TABLE_NAME;
            END;
            $$ LANGUAGE plpgsql;

            CREATE OR REPLACE TRIGGER trg_protect_record_copy_files BEFORE DELETE OR UPDATE ON clinical.record_copy_files
                FOR EACH ROW EXECUTE FUNCTION clinical.protect_record_copy_files();
            CREATE OR REPLACE TRIGGER trg_protect_record_copy_files_truncate BEFORE TRUNCATE ON clinical.record_copy_files
                FOR EACH STATEMENT EXECUTE FUNCTION clinical.protect_record_copy_files();
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "record_copy_files",
                schema: "clinical",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    document_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    document_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    produced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    produced_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    server_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    machine_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    os_description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    software_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_record_copy_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_record_copy_files_record_request_record_request_id",
                        column: x => x.record_request_id,
                        principalSchema: "clinical",
                        principalTable: "record_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_record_copy_files_stored_files_stored_file_id",
                        column: x => x.stored_file_id,
                        principalSchema: "platform",
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_record_copy_files_record_request_id_sequence",
                schema: "clinical",
                table: "record_copy_files",
                columns: new[] { "record_request_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_record_copy_files_sha256",
                schema: "clinical",
                table: "record_copy_files",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "ix_record_copy_files_stored_file_id",
                schema: "clinical",
                table: "record_copy_files",
                column: "stored_file_id");

            migrationBuilder.Sql(Guard);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "record_copy_files",
                schema: "clinical");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS clinical.protect_record_copy_files();");
        }
    }
}
