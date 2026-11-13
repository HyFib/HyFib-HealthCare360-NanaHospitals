using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AbdmUnansweredRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_abdm_link_requests_status_sent_at",
                schema: "interop",
                table: "abdm_link_requests",
                columns: new[] { "status", "sent_at" },
                filter: "status IN ('AwaitingToken', 'Sent')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_abdm_link_requests_status_sent_at",
                schema: "interop",
                table: "abdm_link_requests");
        }
    }
}
