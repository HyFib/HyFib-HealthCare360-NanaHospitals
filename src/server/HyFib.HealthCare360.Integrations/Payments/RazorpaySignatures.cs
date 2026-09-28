namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>
/// Razorpay signature rules (all hex HMAC-SHA256, compared in constant time):
/// <list type="bullet">
/// <item>Checkout: <c>order_id + "|" + razorpay_payment_id</c> signed with the key secret.</item>
/// <item>Payment link callback: <c>plink_id|reference_id|status|payment_id</c> signed with the key secret.</item>
/// <item>Webhook: the raw request body signed with the webhook secret (<c>X-Razorpay-Signature</c>).</item>
/// </list>
/// A JSON body is only ever checked against the webhook secret and a pipe-delimited string only against the key
/// secret, so a signature a browser legitimately received at checkout can never be replayed as a webhook.
/// </summary>
internal static class RazorpaySignatures
{
    public static bool Verify(string payload, string signature, string? keySecret, string? webhookSecret)
    {
        var trimmed = payload.TrimStart();
        var isWebhookBody = trimmed.StartsWith('{') || trimmed.StartsWith('[');
        return Signatures.VerifyHmacSha256Hex(isWebhookBody ? webhookSecret : keySecret, payload, signature);
    }

    public static string CheckoutPayload(string orderId, string paymentId) => $"{orderId}|{paymentId}";

    public static string PaymentLinkPayload(string paymentLinkId, string referenceId, string status, string paymentId) =>
        $"{paymentLinkId}|{referenceId}|{status}|{paymentId}";
}
