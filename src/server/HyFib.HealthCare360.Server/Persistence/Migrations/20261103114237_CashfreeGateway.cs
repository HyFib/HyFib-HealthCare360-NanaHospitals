using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CashfreeGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "gateway_order_id",
                schema: "billing",
                table: "refunds",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_payment_id",
                schema: "billing",
                table: "refunds",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_provider",
                schema: "billing",
                table: "refunds",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_provider",
                schema: "billing",
                table: "payment_disputes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Gateway refunds and dispute cases from before name their gateway too, so they are still followed at it after
            // the facility switches: only Razorpay and the training gateway existed, told apart by their ids as
            // TrainingSimulators does (rfnd_sim…, disp_sim…).
            migrationBuilder.Sql("""
                UPDATE billing.refunds
                SET gateway_provider = CASE WHEN gateway_refund_id LIKE 'rfnd\_sim%' THEN 'simulator' ELSE 'razorpay' END
                WHERE gateway_refund_id IS NOT NULL AND gateway_provider IS NULL;
                UPDATE billing.payment_disputes
                SET gateway_provider = CASE WHEN gateway_dispute_id LIKE 'disp\_sim%' THEN 'simulator' ELSE 'razorpay' END
                WHERE gateway_dispute_id IS NOT NULL AND gateway_provider IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "gateway_order_id",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "gateway_payment_id",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "gateway_provider",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "gateway_provider",
                schema: "billing",
                table: "payment_disputes");
        }
    }
}
