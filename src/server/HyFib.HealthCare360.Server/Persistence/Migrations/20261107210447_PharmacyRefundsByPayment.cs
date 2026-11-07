using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HyFib.HealthCare360.Server.Persistence.Migrations
{
    /// <summary>
    /// Money paid back at the pharmacy goes back the way it came: each refund on an invoice names the payment it pays back
    /// (<c>refund_of_payment_id</c>), a card payment to the card and a UPI payment by UPI, so cash is never paid out for
    /// either. Refunds made before this name none.
    /// </summary>
    public partial class PharmacyRefundsByPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "refund_of_payment_id",
                schema: "pharmacy",
                table: "sale_payments",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refund_of_payment_id",
                schema: "pharmacy",
                table: "sale_payments");
        }
    }
}
