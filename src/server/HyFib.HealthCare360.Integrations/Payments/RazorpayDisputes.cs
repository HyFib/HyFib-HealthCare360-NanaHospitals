using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>
/// Reads Razorpay's disputes (<c>GET v1/disputes/{id}</c>, <c>GET v1/disputes?from&amp;count&amp;skip</c>). Only the ids,
/// amounts, reason code, phase, status and times are kept: the evidence's document ids and the hospital's written answer
/// stay at Razorpay. A dispute whose id or payment id does not look like Razorpay's is left out, so nothing untrusted ever
/// reaches a URL or a query.
/// </summary>
internal static partial class RazorpayDisputes
{
    /// <summary>Razorpay lists at most 100 items per call.</summary>
    public const int PageSize = 100;

    /// <summary>More disputes than this since the date asked is refused rather than half read.</summary>
    public const int MaxPages = 50;

    public static GatewayDispute? Read(JsonElement element)
    {
        var id = IntegrationHttp.Property(element, "id");
        var paymentId = IntegrationHttp.Property(element, "payment_id");
        if (!IsDisputeId(id) || paymentId is null || !PaymentIdPattern().IsMatch(paymentId))
        {
            return null;
        }

        var status = Word(IntegrationHttp.Property(element, "status"));
        return new GatewayDispute(
            id!,
            paymentId,
            PaymentAmounts.FromPaise(RazorpayStatus.ReadLong(element, "amount")),
            Code(IntegrationHttp.Property(element, "reason_code")),
            Word(IntegrationHttp.Property(element, "phase")),
            StatusOf(status),
            status,
            RazorpayStatus.ReadTime(element, "respond_by"),
            RazorpayStatus.ReadTime(element, "created_at"),
            element.TryGetProperty("evidence", out var evidence) ? RazorpayStatus.ReadTime(evidence, "submitted_at") : null);
    }

    /// <summary>The disputes of one page of the list; items that cannot be read are left out.</summary>
    public static IReadOnlyList<GatewayDispute> ReadPage(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new IntegrationException("Razorpay's list of disputes could not be read.");
        }

        return items.EnumerateArray().Select(Read).OfType<GatewayDispute>().ToList();
    }

    /// <summary>The number of items the page held, which decides whether another page is needed.</summary>
    public static int CountItems(JsonElement root) =>
        root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;

    /// <summary>
    /// <c>open</c> (a draft answer saved at Razorpay is still open), <c>under_review</c> once the answer is submitted,
    /// then <c>won</c> or <c>lost</c>; anything else (<c>closed</c>, or a word Razorpay adds later) is for a person to read.
    /// </summary>
    public static GatewayDisputeStatus StatusOf(string? status) => status switch
    {
        "open" => GatewayDisputeStatus.Open,
        "under_review" => GatewayDisputeStatus.UnderReview,
        "won" => GatewayDisputeStatus.Won,
        "lost" => GatewayDisputeStatus.Lost,
        _ => GatewayDisputeStatus.Other,
    };

    public static bool IsDisputeId(string? id) => id is not null && DisputeIdPattern().IsMatch(id);

    /// <summary>A dispute id from a settlement's transaction, or null when it does not look like one.</summary>
    public static string? SafeId(string? id) => IsDisputeId(id) ? id : null;

    /// <summary>Network reason codes (10.4, 4837, 13.1) and Razorpay's own (chargeback, pre_arbitration).</summary>
    private static string? Code(string? code) => code is not null && CodePattern().IsMatch(code) ? code : null;

    /// <summary>A status or phase: a short lower-case word.</summary>
    private static string? Word(string? word) => word is not null && WordPattern().IsMatch(word) ? word : null;

    [GeneratedRegex("^disp_[A-Za-z0-9]{6,40}$")]
    private static partial Regex DisputeIdPattern();

    [GeneratedRegex("^pay_[A-Za-z0-9]{6,40}$")]
    private static partial Regex PaymentIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,32}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z_]{2,32}$")]
    private static partial Regex WordPattern();
}
