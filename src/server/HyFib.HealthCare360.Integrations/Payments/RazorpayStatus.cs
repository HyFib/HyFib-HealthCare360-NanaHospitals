namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>One payment attempt as Razorpay reports it (orders' <c>/payments</c> or a payment link's <c>payments</c>).</summary>
internal sealed record RazorpayPayment(string Id, string Status, long AmountPaise, long AmountRefundedPaise, string? Method, string? Error);

/// <summary>
/// Collapses Razorpay's per-attempt payment states into one order status. A patient may fail a card attempt and then
/// pay by UPI, so any captured attempt wins over earlier failures; an order only "fails" when every attempt failed.
/// </summary>
internal static class RazorpayStatus
{
    public static PaymentStatusResult FromPayments(string orderId, IReadOnlyList<RazorpayPayment> payments)
    {
        var captured = payments.FirstOrDefault(p => p.Status == "captured");
        if (captured is not null)
        {
            var fullyRefunded = captured.AmountPaise > 0 && captured.AmountRefundedPaise >= captured.AmountPaise;
            return new PaymentStatusResult(orderId, fullyRefunded ? GatewayPaymentStatus.Refunded : GatewayPaymentStatus.Captured, captured.Id,
                PaymentAmounts.FromPaise(captured.AmountPaise - captured.AmountRefundedPaise), captured.Method, null);
        }

        if (payments.FirstOrDefault(p => p.Status == "refunded") is { } refunded)
        {
            return new PaymentStatusResult(orderId, GatewayPaymentStatus.Refunded, refunded.Id, 0m, refunded.Method, null);
        }

        if (payments.FirstOrDefault(p => p.Status == "authorized") is { } authorized)
        {
            // Authorised but not yet captured (auto-capture pending or manual capture configured).
            return new PaymentStatusResult(orderId, GatewayPaymentStatus.Pending, authorized.Id, null, authorized.Method, null);
        }

        if (payments.Count > 0 && payments.All(p => p.Status == "failed"))
        {
            var last = payments[^1];
            return new PaymentStatusResult(orderId, GatewayPaymentStatus.Failed, last.Id, null, last.Method, last.Error ?? "Payment failed.");
        }

        return new PaymentStatusResult(orderId, payments.Count == 0 ? GatewayPaymentStatus.Created : GatewayPaymentStatus.Pending, null, null, null, null);
    }

    /// <summary>Payment link states: created, partially_paid, paid, expired, cancelled.</summary>
    public static PaymentStatusResult FromPaymentLink(string linkId, string? linkStatus, IReadOnlyList<RazorpayPayment> payments)
    {
        switch (linkStatus)
        {
            case "paid":
                var paid = FromPayments(linkId, payments);
                return paid.Status is GatewayPaymentStatus.Captured or GatewayPaymentStatus.Refunded
                    ? paid
                    : new PaymentStatusResult(linkId, GatewayPaymentStatus.Captured, payments.FirstOrDefault()?.Id, null, payments.FirstOrDefault()?.Method, null);
            case "partially_paid":
                return new PaymentStatusResult(linkId, GatewayPaymentStatus.Pending, payments.FirstOrDefault()?.Id,
                    PaymentAmounts.FromPaise(payments.Where(p => p.Status == "captured").Sum(p => p.AmountPaise)), null, null);
            case "expired":
            case "cancelled":
                return new PaymentStatusResult(linkId, GatewayPaymentStatus.Failed, null, null, null, $"The payment link was {linkStatus}.");
            default:
                return FromPayments(linkId, payments);
        }
    }

    public static IReadOnlyList<RazorpayPayment> ReadPayments(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(p => new RazorpayPayment(
                IntegrationHttp.Property(p, "id") ?? IntegrationHttp.Property(p, "payment_id") ?? string.Empty,
                IntegrationHttp.Property(p, "status") ?? string.Empty,
                ReadLong(p, "amount"),
                ReadLong(p, "amount_refunded"),
                IntegrationHttp.Property(p, "method"),
                IntegrationHttp.Property(p, "error_description")))
            .Where(p => p.Id.Length > 0)
            .ToList();
    }

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
