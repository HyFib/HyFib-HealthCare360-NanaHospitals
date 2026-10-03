using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>A settlement transaction as the recon API lists it, before it is grouped under its settlement.</summary>
internal sealed record RazorpayReconItem(string SettlementId, string? Utr, DateTimeOffset? SettledAt, PaymentSettlementItem Item);

/// <summary>
/// Reads Razorpay's settlement reconciliation data (<c>GET v1/settlements/recon/combined</c> and
/// <c>GET v1/settlements/{id}</c>). Only ids, amounts, the UTR and the payment method are kept: the card network,
/// issuer and type, the description and the notes the API also returns are left behind, because reconciliation does
/// not need them and notes can carry what staff typed about a patient.
/// </summary>
internal static partial class RazorpaySettlements
{
    /// <summary>The recon API returns at most 1,000 items per call.</summary>
    public const int PageSize = 1000;

    /// <summary>A day with more transactions than this is refused rather than half read (100,000 payments a day).</summary>
    public const int MaxPages = 100;

    public static IReadOnlyList<RazorpayReconItem> ReadReconPage(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new IntegrationException("Razorpay's settlement report could not be read.");
        }

        var result = new List<RazorpayReconItem>();
        foreach (var element in items.EnumerateArray())
        {
            var settlementId = IntegrationHttp.Property(element, "settlement_id");
            var entityId = IntegrationHttp.Property(element, "entity_id");
            if (settlementId is null || !SettlementIdPattern().IsMatch(settlementId) || entityId is null || !EntityIdPattern().IsMatch(entityId))
            {
                // Not settled yet, or not an id we could ever match: nothing to reconcile.
                continue;
            }

            var item = new PaymentSettlementItem(
                entityId,
                TypeOf(IntegrationHttp.Property(element, "type")),
                SafeId(IntegrationHttp.Property(element, "payment_id")),
                SafeId(IntegrationHttp.Property(element, "order_id")),
                Paise(element, "amount"),
                Paise(element, "fee"),
                Paise(element, "tax"),
                Paise(element, "credit"),
                Paise(element, "debit"),
                Method(IntegrationHttp.Property(element, "method")),
                RazorpayStatus.ReadTime(element, "created_at"));
            result.Add(new RazorpayReconItem(settlementId, Utr(IntegrationHttp.Property(element, "settlement_utr")), RazorpayStatus.ReadTime(element, "settled_at"), item));
        }

        return result;
    }

    /// <summary>The number of items the page held (settled or not), which decides whether another page is needed.</summary>
    public static int CountItems(JsonElement root) =>
        root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;

    /// <summary>Builds the settlement from its header (<c>GET v1/settlements/{id}</c>) and the items the recon API listed for it.</summary>
    public static PaymentSettlement Build(JsonElement header, string settlementId, IReadOnlyList<RazorpayReconItem> items)
    {
        var settledAt = RazorpayStatus.ReadTime(header, "created_at")
            ?? items.Select(i => i.SettledAt).Where(t => t is not null).Max()
            ?? throw new IntegrationException($"Razorpay did not say when settlement {settlementId} was made.");
        return new PaymentSettlement(
            settlementId,
            Utr(IntegrationHttp.Property(header, "utr")) ?? items.Select(i => i.Utr).FirstOrDefault(u => u is not null),
            StatusOf(IntegrationHttp.Property(header, "status")),
            Paise(header, "amount"),
            Paise(header, "fees"),
            Paise(header, "tax"),
            settledAt,
            items.Select(i => i.Item).ToList());
    }

    public static bool IsSettlementId(string? id) => id is not null && SettlementIdPattern().IsMatch(id);

    public static PaymentSettlementStatus StatusOf(string? status) => status switch
    {
        "processed" => PaymentSettlementStatus.Processed,
        "failed" => PaymentSettlementStatus.Failed,
        _ => PaymentSettlementStatus.Created,
    };

    public static PaymentTransactionType TypeOf(string? type) => type switch
    {
        "payment" => PaymentTransactionType.Payment,
        "refund" => PaymentTransactionType.Refund,
        "adjustment" => PaymentTransactionType.Adjustment,
        "transfer" => PaymentTransactionType.Transfer,
        _ => PaymentTransactionType.Other,
    };

    private static decimal Paise(JsonElement element, string name) => PaymentAmounts.FromPaise(RazorpayStatus.ReadLong(element, name));

    private static string? SafeId(string? id) => id is not null && EntityIdPattern().IsMatch(id) ? id : null;

    /// <summary>UTRs are bank references: letters and digits only, at most 64.</summary>
    private static string? Utr(string? utr) => utr is not null && UtrPattern().IsMatch(utr) ? utr : null;

    /// <summary>card, upi, netbanking, wallet, emi… (a short lower-case word; anything else is dropped).</summary>
    private static string? Method(string? method) => method is not null && MethodPattern().IsMatch(method) ? method : null;

    [GeneratedRegex("^setl_[A-Za-z0-9]{6,40}$")]
    private static partial Regex SettlementIdPattern();

    [GeneratedRegex("^[a-z]{2,8}_[A-Za-z0-9]{6,40}$")]
    private static partial Regex EntityIdPattern();

    [GeneratedRegex("^[A-Za-z0-9]{4,64}$")]
    private static partial Regex UtrPattern();

    [GeneratedRegex("^[a-z_]{2,20}$")]
    private static partial Regex MethodPattern();
}
