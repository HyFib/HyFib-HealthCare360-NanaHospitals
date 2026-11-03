using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>A payment of a Cashfree order (<c>GET /orders/{order_id}/payments</c>): only what HC360 records is kept.</summary>
internal sealed record CashfreePayment(
    string PaymentId, string OrderId, string? Status, decimal Amount, string? Method, string? Message, DateTimeOffset? At);

/// <summary>
/// Reads Cashfree's responses (x-api-version 2026-01-01, as the official SDKs cashfree-pg Node 6.0.6 and Python 6.0.1
/// describe them). Where the SDKs disagree, both shapes are read: ids are strings in the schema but integers in the
/// guides' samples, so both are taken; a recon row's UTR is <c>settlement_utr</c> or <c>utr</c>, its payment method
/// <c>payment_group</c> or <c>payment_mode</c>, its refund an object or a list. Only ids, amounts, statuses, times and
/// the payment method are kept: card, bank and customer details Cashfree also returns are left behind.
/// </summary>
internal static partial class CashfreeReading
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    /// <summary>A string or number property as text (Cashfree's ids come as either).</summary>
    public static string? Text(JsonElement element, string name) => IntegrationHttp.Property(element, name);

    public static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    /// <summary>A rupee amount given as a number or a numeric string; absent or unreadable reads as zero.</summary>
    public static decimal Amount(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m,
        };
    }

    /// <summary>
    /// A time as Cashfree writes it: ISO 8601 with an offset (<c>2023-08-11T18:02:46+05:30</c>, <c>…Z</c>), or without one
    /// (<c>2022-08-16 14:53:15</c>), which is Indian time ("we store timestamps in IST").
    /// </summary>
    public static DateTimeOffset? Time(JsonElement element, string name) => TimeOf(Text(element, name));

    public static DateTimeOffset? TimeOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (HasOffset().IsMatch(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset))
        {
            return withOffset;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)
            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), India)
            : null;
    }

    /// <summary>The payments of an order; entries without a usable id are left out.</summary>
    public static IReadOnlyList<CashfreePayment> Payments(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<CashfreePayment>();
        foreach (var p in array.EnumerateArray())
        {
            var id = SafeId(Text(p, "cf_payment_id"));
            var order = SafeId(Text(p, "order_id"));
            if (id is null || order is null)
            {
                continue;
            }

            var error = Child(p, "error_details");
            result.Add(new CashfreePayment(
                id,
                order,
                Text(p, "payment_status"),
                Amount(p, "payment_amount"),
                Method(Text(p, "payment_group")),
                Words(Text(p, "payment_message")) ?? Words(Text(error, "error_description")),
                Time(p, "payment_completion_time") ?? Time(p, "payment_time")));
        }

        return result;
    }

    /// <summary>
    /// Where a link stands from its payments: a successful payment wins (money taken is never reported as unpaid); then
    /// a cancelled or expired link; then a payment the bank has not finished; then the last failed attempt (the link stays
    /// open for another); otherwise nobody has paid yet.
    /// </summary>
    public static PaymentStatusResult LinkStatus(
        string linkId, string? linkStatus, DateTimeOffset? expiresAt, DateTimeOffset now, IReadOnlyList<CashfreePayment> payments)
    {
        var paid = payments.Where(p => p.Status == "SUCCESS").OrderBy(p => p.At ?? DateTimeOffset.MaxValue).FirstOrDefault();
        if (paid is not null)
        {
            return new PaymentStatusResult(linkId, GatewayPaymentStatus.Captured, paid.PaymentId, paid.Amount, paid.Method, null, paid.At);
        }

        if (linkStatus is "CANCELLED")
        {
            return new PaymentStatusResult(linkId, GatewayPaymentStatus.Cancelled, null, null, null, "The payment link was cancelled at Cashfree.");
        }

        if (linkStatus is "EXPIRED" || (expiresAt is { } expiry && expiry <= now))
        {
            return new PaymentStatusResult(linkId, GatewayPaymentStatus.Expired, null, null, null, "The payment link expired unpaid.");
        }

        if (payments.Any(p => p.Status == "PENDING"))
        {
            return new PaymentStatusResult(linkId, GatewayPaymentStatus.Pending, null, null, null, "A payment is waiting for the bank.");
        }

        var failed = payments.Where(p => p.Status is "FAILED" or "USER_DROPPED" or "CANCELLED" or "VOID")
            .OrderByDescending(p => p.At ?? DateTimeOffset.MinValue).FirstOrDefault();
        return failed is not null
            ? new PaymentStatusResult(linkId, GatewayPaymentStatus.Failed, null, null, failed.Method, failed.Message ?? "The last payment attempt did not go through.")
            : new PaymentStatusResult(linkId, GatewayPaymentStatus.Created, null, null, null, null);
    }

    /// <summary><c>SUCCESS</c> is with the patient's bank; <c>PENDING</c> and <c>ONHOLD</c> wait; <c>CANCELLED</c> and <c>FAILED</c> did not happen.</summary>
    public static RefundStatusResult Refund(string refundId, JsonElement refund)
    {
        var status = Text(refund, "refund_status");
        var mapped = status switch
        {
            "SUCCESS" => PaymentRefundStatus.Processed,
            "CANCELLED" or "FAILED" => PaymentRefundStatus.Failed,
            _ => PaymentRefundStatus.Pending,
        };
        return new RefundStatusResult(
            refundId,
            mapped,
            Amount(refund, "refund_amount") is > 0 and var amount ? amount : null,
            SafeId(Text(refund, "cf_payment_id")),
            Reference(Text(refund, "refund_arn")),
            Time(refund, "created_at"),
            mapped == PaymentRefundStatus.Failed ? Words(Text(refund, "status_description")) ?? $"Cashfree reports the refund {status?.ToLowerInvariant()}." : null);
    }

    /// <summary>
    /// A dispute (<c>DisputesEntity</c>). Its status is <c>&lt;TYPE&gt;_&lt;STAGE&gt;</c>: created → open; documents
    /// received or under review → the hospital's answer is in; merchant won → won; merchant lost or accepted → lost;
    /// insufficient evidence, or a stage HC360 does not know → for a person. The phase is the type (dispute,
    /// chargeback, retrieval, pre-arbitration, arbitration); the status text is the stage.
    /// </summary>
    public static GatewayDispute? Dispute(JsonElement element)
    {
        var id = Text(element, "dispute_id");
        var order = Child(element, "order_details");
        var paymentId = SafeId(Text(order, "cf_payment_id"));
        if (id is null || !DisputeIdPattern().IsMatch(id) || paymentId is null)
        {
            return null;
        }

        var type = Text(element, "dispute_type");
        var full = Text(element, "dispute_status");
        var stage = full is not null && type is not null && full.StartsWith(type + "_", StringComparison.Ordinal) ? full[(type.Length + 1)..] : full;
        var status = stage switch
        {
            "CREATED" => GatewayDisputeStatus.Open,
            "DOCS_RECEIVED" or "UNDER_REVIEW" => GatewayDisputeStatus.UnderReview,
            "MERCHANT_WON" => GatewayDisputeStatus.Won,
            "MERCHANT_LOST" or "MERCHANT_ACCEPTED" => GatewayDisputeStatus.Lost,
            _ => GatewayDisputeStatus.Other,
        };
        return new GatewayDispute(
            id,
            paymentId,
            Amount(element, "dispute_amount"),
            Code(Text(element, "reason_code")),
            Word(type?.ToLowerInvariant()),
            status,
            Word(stage?.ToLowerInvariant()),
            Time(element, "respond_by"),
            Time(element, "created_at"),
            status == GatewayDisputeStatus.UnderReview ? Time(element, "updated_at") : null);
    }

    public static IReadOnlyList<GatewayDispute> Disputes(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(Dispute).OfType<GatewayDispute>().ToList() : [];

    /// <summary>One settlement of <c>POST /settlements</c>, and the Indian day it was paid to the bank.</summary>
    public static (string Id, string? Utr, PaymentSettlementStatus Status, decimal Amount, decimal Fees, decimal Tax, DateTimeOffset? At)? Settlement(JsonElement element)
    {
        var id = Text(element, "cf_settlement_id");
        if (id is null || !SettlementIdPattern().IsMatch(id))
        {
            return null;
        }

        var status = Text(element, "status") switch
        {
            "SUCCESS" => PaymentSettlementStatus.Processed,
            "FAILED" => PaymentSettlementStatus.Failed,
            _ => PaymentSettlementStatus.Created,
        };
        var tax = Amount(element, "settlement_tax");
        return (id, Reference(Text(element, "settlement_utr") ?? Text(element, "utr")), status, Amount(element, "amount_settled"),
            Amount(element, "settlement_charge") + tax, tax,
            Time(element, "settlement_processed_on") ?? Time(element, "settlement_initiated_on"));
    }

    /// <summary>
    /// A settlement's event (<c>POST /settlement/recon</c> or <c>POST /recon</c>) as a transaction. Money in
    /// (payments, reversals of refunds, disputes and chargebacks) credits the settlement; refunds, disputes and
    /// chargebacks debit it; another adjustment goes by its sale type or the sign of its amount. Events with no id HC360
    /// could ever match are left out.
    /// </summary>
    public static PaymentSettlementItem? Event(JsonElement row)
    {
        var details = Child(row, "event_details");
        var type = Text(details, "event_type") is { } named && EventTypePattern().IsMatch(named) ? named : "OTHER";
        var payment = Child(row, "payment_details");
        var paymentId = SafeId(Text(payment, "cf_payment_id"));
        var orderId = SafeId(Text(Child(row, "order_details"), "order_id"));
        var eventId = SafeId(Text(details, "event_id"));
        var amount = Math.Abs(Amount(details, "event_amount"));
        var settled = Amount(details, "event_settlement_amount");
        var tax = Math.Abs(Amount(details, "event_service_tax"));
        var fee = Math.Abs(Amount(details, "event_service_charge")) + tax;
        var at = Time(details, "event_time");
        var method = Method(Text(payment, "payment_group") ?? Text(payment, "payment_mode"));
        bool credit = type switch
        {
            "PAYMENT" or "REFUND_REVERSAL" or "DISPUTE_REVERSAL" or "CHARGEBACK_REVERSAL" => true,
            "REFUND" or "DISPUTE" or "CHARGEBACK" => false,
            _ => Text(details, "sale_type") is { } sale ? sale.Equals("CREDIT", StringComparison.OrdinalIgnoreCase) : settled >= 0,
        };
        var net = settled != 0 ? Math.Abs(settled) : credit ? Math.Max(amount - fee, 0m) : amount;
        switch (type)
        {
            case "PAYMENT" when paymentId is not null:
                return new PaymentSettlementItem(paymentId, PaymentTransactionType.Payment, paymentId, orderId, amount, fee, tax, net, 0m, method, at);
            case "REFUND" when RefundId(row) is { } refundId:
                return new PaymentSettlementItem(refundId, PaymentTransactionType.Refund, paymentId, orderId, amount, fee, tax, 0m, net, method, at);
            default:
                var id = eventId ?? paymentId;
                return id is null
                    ? null
                    : new PaymentSettlementItem(
                        AdjustmentId(type, id), PaymentTransactionType.Adjustment, paymentId, orderId, amount, fee, tax,
                        credit ? net : 0m, credit ? 0m : net, method, at);
        }
    }

    /// <summary>An adjustment's id: its event (or payment) and kind, at most 64 characters, the same on every read.</summary>
    private static string AdjustmentId(string type, string id)
    {
        var key = $"cfevt_{type.ToLowerInvariant()}_{id}";
        return key.Length <= 64
            ? key
            : "cfevt_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..40];
    }

    /// <summary>The row's refund: an object in <c>/recon</c>, a list in <c>/settlement/recon</c>.</summary>
    private static string? RefundId(JsonElement row)
    {
        var refund = Child(row, "refund_details");
        if (refund.ValueKind == JsonValueKind.Array)
        {
            refund = refund.EnumerateArray().FirstOrDefault();
        }

        return SafeId(Text(refund, "refund_id"));
    }

    /// <summary>The next page's cursor, or null when the page was the last (no cursor, or fewer rows than asked for).</summary>
    public static string? NextCursor(JsonElement root, int asked)
    {
        var rows = Child(root, "data");
        var count = rows.ValueKind == JsonValueKind.Array ? rows.GetArrayLength() : 0;
        var cursor = Text(root, "cursor");
        return count < asked || string.IsNullOrWhiteSpace(cursor) || cursor.Length > 2048 ? null : cursor;
    }

    /// <summary>An id HC360 may store, compare or put in a URL: letters, digits, <c>_</c> and <c>-</c>, at most 64.</summary>
    public static string? SafeId(string? id) => id is not null && IdPattern().IsMatch(id) ? id : null;

    public static bool IsDisputeId(string? id) => id is not null && DisputeIdPattern().IsMatch(id);

    /// <summary>The bank's reference (UTR, ARN): letters and digits, at most 64.</summary>
    private static string? Reference(string? value) => value is not null && ReferencePattern().IsMatch(value) ? value : null;

    /// <summary>upi, credit_card, net_banking… (a short word, lower case); anything else is dropped.</summary>
    private static string? Method(string? method) =>
        method?.Trim().ToLowerInvariant() is { } word && MethodPattern().IsMatch(word) ? word : null;

    /// <summary>A status message for staff: at most 200 characters, one line.</summary>
    private static string? Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : IntegrationTrace.Truncate(text.ReplaceLineEndings(" ").Trim(), 200);

    /// <summary>Network reason codes (10.4, 4837, 13.1) and Cashfree's words for them.</summary>
    private static string? Code(string? code) => code is not null && CodePattern().IsMatch(code) ? code : null;

    private static string? Word(string? word) => word is not null && WordPattern().IsMatch(word) ? word : null;

    [GeneratedRegex("[+-][0-9]{2}:?[0-9]{2}$|Z$")]
    private static partial Regex HasOffset();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9]{1,18}$")]
    private static partial Regex DisputeIdPattern();

    [GeneratedRegex("^[A-Z_]{2,32}$")]
    private static partial Regex EventTypePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SettlementIdPattern();

    [GeneratedRegex("^[A-Za-z0-9]{4,64}$")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex("^[a-z_]{2,32}$")]
    private static partial Regex MethodPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,32}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z_]{2,32}$")]
    private static partial Regex WordPattern();
}
