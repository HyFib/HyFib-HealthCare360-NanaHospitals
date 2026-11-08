using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MortuaryReleaseWitness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "release_witness_name",
                schema: "supportservices",
                table: "body_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "release_witness_user_id",
                schema: "supportservices",
                table: "body_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "released_by_user_id",
                schema: "supportservices",
                table: "body_records",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "release_witness_name",
                schema: "supportservices",
                table: "body_records");

            migrationBuilder.DropColumn(
                name: "release_witness_user_id",
                schema: "supportservices",
                table: "body_records");

            migrationBuilder.DropColumn(
                name: "released_by_user_id",
                schema: "supportservices",
                table: "body_records");
        }
    }
}
