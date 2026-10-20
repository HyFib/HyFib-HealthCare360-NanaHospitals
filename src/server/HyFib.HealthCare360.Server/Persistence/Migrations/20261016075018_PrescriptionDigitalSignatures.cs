using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrescriptionDigitalSignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "digital_signature",
                schema: "clinical",
                table: "prescriptions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "digitally_signed_at",
                schema: "clinical",
                table: "prescriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "digitally_signed_by_user_id",
                schema: "clinical",
                table: "prescriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "signed_content",
                schema: "clinical",
                table: "prescriptions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signed_content_hash",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_chain_status",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_issuer",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_name",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_serial_number",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_subject",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_thumbprint",
                schema: "clinical",
                table: "prescriptions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "digital_signature",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "digitally_signed_at",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "digitally_signed_by_user_id",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "signed_content",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "signed_content_hash",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "signer_chain_status",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "signer_issuer",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "signer_name",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "signer_serial_number",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "signer_subject",
                schema: "clinical",
                table: "prescriptions");

            migrationBuilder.DropColumn(
                name: "signer_thumbprint",
                schema: "clinical",
                table: "prescriptions");
        }
    }
}
