namespace HyFib.HealthCare360.Integrations.Pos;

/// <summary>A Plutus Cloud response: code, message, provider reference and the tag/value transaction data.</summary>
internal sealed record PineLabsResponse(int ResponseCode, string? ResponseMessage, long ReferenceId, IReadOnlyDictionary<string, string> Data);

/// <summary>Parses Plutus Cloud responses and maps them to HC360 POS states.</summary>
internal static class PineLabsResponses
{
    /// <summary>Default "transaction uploaded, waiting for the customer" code.</summary>
    public const int Pending = 1001;

    public static PineLabsResponse? Parse(string body)
    {
        if (!IntegrationHttp.TryParseJson(body, out var document))
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!int.TryParse(IntegrationHttp.Property(root, "ResponseCode"), out var code))
            {
                return null;
            }

            _ = long.TryParse(IntegrationHttp.Property(root, "PlutusTransactionReferenceID"), out var reference);
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("TransactionData", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (IntegrationHttp.Property(item, "Tag") is { } tag && IntegrationHttp.Property(item, "Value") is { } value)
                    {
                        data[Normalize(tag)] = value;
                    }
                }
            }

            return new PineLabsResponse(code, IntegrationHttp.Property(root, "ResponseMessage"), reference, data);
        }
    }

    /// <summary>Maps a status (or cancel) response. Code 0 is approved; pending codes mean the customer has not paid yet.</summary>
    public static PosTransactionResult MapStatus(string transactionId, PineLabsResponse response, IReadOnlyCollection<int> pendingCodes)
    {
        if (response.ResponseCode == 0)
        {
            return new PosTransactionResult(transactionId, PosTransactionStatus.Approved, Tag(response, "ApprovalCode"),
                Last4(Tag(response, "CardNumber")), Tag(response, "RRN"), response.ResponseMessage);
        }

        if (pendingCodes.Contains(response.ResponseCode))
        {
            return new PosTransactionResult(transactionId, PosTransactionStatus.AwaitingCustomer, null, null, null, response.ResponseMessage);
        }

        var message = response.ResponseMessage?.ToUpperInvariant() ?? string.Empty;
        var status = message.Contains("CANCEL", StringComparison.Ordinal) ? PosTransactionStatus.Cancelled
            : message.Contains("TIME", StringComparison.Ordinal) || message.Contains("EXPIRE", StringComparison.Ordinal) ? PosTransactionStatus.TimedOut
            : PosTransactionStatus.Declined;
        return new PosTransactionResult(transactionId, status, null, null, null, response.ResponseMessage ?? $"Terminal response {response.ResponseCode}");
    }

    internal static string? Last4(string? cardNumber)
    {
        var digits = new string((cardNumber ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : null;
    }

    private static string? Tag(PineLabsResponse response, string tag) => response.Data.GetValueOrDefault(Normalize(tag));

    private static string Normalize(string tag) => new(tag.Where(char.IsLetterOrDigit).ToArray());
}
