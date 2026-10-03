using System.Text.Json;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Payments;

public sealed class RazorpayStatusTests
{
    private static RazorpayPayment Payment(string id, string status, long paise = 50_000, long refunded = 0, string? error = null) =>
        new(id, status, paise, refunded, "upi", error);

    [Fact]
    public void An_order_without_attempts_is_created() =>
        RazorpayStatus.FromPayments("order_A", []).Status.ShouldBe(GatewayPaymentStatus.Created);

    [Fact]
    public void A_captured_attempt_wins_over_earlier_failures()
    {
        var status = RazorpayStatus.FromPayments("order_A", [Payment("pay_1", "failed", error: "Card declined"), Payment("pay_2", "captured")]);

        status.ShouldBe(new PaymentStatusResult("order_A", GatewayPaymentStatus.Captured, "pay_2", 500m, "upi", null));
    }

    [Fact]
    public void Partial_refunds_reduce_the_amount_paid_and_full_refunds_mark_the_order_refunded()
    {
        RazorpayStatus.FromPayments("order_A", [Payment("pay_1", "captured", refunded: 20_000)]).AmountPaid.ShouldBe(300m);

        var refunded = RazorpayStatus.FromPayments("order_A", [Payment("pay_1", "captured", refunded: 50_000)]);
        refunded.Status.ShouldBe(GatewayPaymentStatus.Refunded);
        refunded.AmountPaid.ShouldBe(0m);
        RazorpayStatus.FromPayments("order_A", [Payment("pay_1", "refunded")]).Status.ShouldBe(GatewayPaymentStatus.Refunded);
    }

    [Fact]
    public void Authorised_payments_are_still_pending() =>
        RazorpayStatus.FromPayments("order_A", [Payment("pay_1", "authorized")]).Status.ShouldBe(GatewayPaymentStatus.Pending);

    [Fact]
    public void An_order_fails_only_when_every_attempt_failed()
    {
        var failed = RazorpayStatus.FromPayments("order_A", [Payment("pay_1", "failed", error: "Card declined"), Payment("pay_2", "failed", error: "UPI timed out")]);

        failed.Status.ShouldBe(GatewayPaymentStatus.Failed);
        failed.PaymentId.ShouldBe("pay_2");
        failed.Error.ShouldBe("UPI timed out");
        RazorpayStatus.FromPayments("order_A", [Payment("pay_1", "failed"), Payment("pay_2", "created")]).Status.ShouldBe(GatewayPaymentStatus.Pending);
    }

    [Fact]
    public void Maps_payment_link_states()
    {
        RazorpayStatus.FromPaymentLink("plink_A", "paid", [Payment("pay_1", "captured")]).Status.ShouldBe(GatewayPaymentStatus.Captured);
        RazorpayStatus.FromPaymentLink("plink_A", "paid", []).Status.ShouldBe(GatewayPaymentStatus.Captured);
        RazorpayStatus.FromPaymentLink("plink_A", "created", []).Status.ShouldBe(GatewayPaymentStatus.Created);

        var partial = RazorpayStatus.FromPaymentLink("plink_A", "partially_paid", [Payment("pay_1", "captured", 20_000), Payment("pay_2", "failed")]);
        partial.Status.ShouldBe(GatewayPaymentStatus.Pending);
        partial.AmountPaid.ShouldBe(200m);

        var expired = RazorpayStatus.FromPaymentLink("plink_A", "expired", []);
        expired.Status.ShouldBe(GatewayPaymentStatus.Expired);
        expired.Error.ShouldBe("The payment link expired before it was paid.");

        var cancelled = RazorpayStatus.FromPaymentLink("plink_A", "cancelled", [Payment("pay_1", "failed")]);
        cancelled.Status.ShouldBe(GatewayPaymentStatus.Cancelled);
        cancelled.Error.ShouldBe("The payment link was cancelled at the gateway.");
    }

    [Fact]
    public void A_payment_captured_just_before_a_link_closed_is_still_money_taken()
    {
        var expired = RazorpayStatus.FromPaymentLink("plink_A", "expired", [Payment("pay_1", "captured")]);

        expired.Status.ShouldBe(GatewayPaymentStatus.Captured);
        expired.PaymentId.ShouldBe("pay_1");
    }

    [Fact]
    public void A_capture_reports_when_the_patient_paid()
    {
        var paidAt = new DateTimeOffset(2026, 9, 30, 4, 55, 12, TimeSpan.Zero);

        var status = RazorpayStatus.FromPayments("order_A", [new RazorpayPayment("pay_1", "captured", 50_000, 0, "upi", null, paidAt)]);

        status.PaidAt.ShouldBe(paidAt);
        RazorpayStatus.FromPaymentLink("plink_A", "paid", [new RazorpayPayment("pay_1", "captured", 50_000, 0, "upi", null, paidAt)]).PaidAt.ShouldBe(paidAt);
    }

    [Theory]
    [InlineData("""{"created_at":1759208112}""", 1759208112L)]
    [InlineData("""{"created_at":0}""", null)]
    [InlineData("""{"created_at":"1759208112"}""", null)]
    [InlineData("""{}""", null)]
    public void Reads_unix_timestamps_and_ignores_anything_else(string json, long? expected)
    {
        using var document = JsonDocument.Parse(json);

        RazorpayStatus.ReadTime(document.RootElement, "created_at").ShouldBe(expected is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
    }

    [Fact]
    public void Reads_payment_arrays_from_orders_and_links()
    {
        using var document = JsonDocument.Parse("""
            [
              {"id":"pay_1","status":"captured","amount":150050,"amount_refunded":0,"method":"card","created_at":1759208112},
              {"payment_id":"pay_2","status":"failed","amount":150050,"error_description":"Payment was cancelled"},
              {"status":"created"}
            ]
            """);

        var payments = RazorpayStatus.ReadPayments(document.RootElement);

        payments.Count.ShouldBe(2);
        payments[0].ShouldBe(new RazorpayPayment("pay_1", "captured", 150050, 0, "card", null, DateTimeOffset.FromUnixTimeSeconds(1759208112)));
        payments[1].Id.ShouldBe("pay_2");
        payments[1].Error.ShouldBe("Payment was cancelled");
    }

    [Fact]
    public void Non_array_payment_lists_are_empty()
    {
        using var document = JsonDocument.Parse("""{"count":0}""");

        RazorpayStatus.ReadPayments(document.RootElement).ShouldBeEmpty();
    }
}
