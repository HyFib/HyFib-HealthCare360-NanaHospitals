using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Pos;

/// <summary>
/// Non-secret POS configuration (<c>integrations.pos</c>) for a Pine Labs Plutus Cloud-style push-to-terminal API
/// (INT-POS). Endpoint paths are configurable because acquirers expose the same flow under different URLs.
/// </summary>
internal sealed record PosSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>pinelabs</c> or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    public string BaseUrl { get; init; } = "https://www.plutuscloudserviceuat.in:8201";

    public string UploadPath { get; init; } = "/API/CloudBasedIntegration/V1/UploadBilledTransaction";

    public string StatusPath { get; init; } = "/API/CloudBasedIntegration/V1/GetCloudBasedTxnStatus";

    public string CancelPath { get; init; } = "/API/CloudBasedIntegration/V1/CancelTransaction";

    public string? MerchantId { get; init; }

    public string? UserId { get; init; }

    /// <summary>The terminal cancels an unpaid transaction after this many minutes.</summary>
    public int AutoCancelMinutes { get; init; } = 5;

    public IReadOnlyList<PosTerminalSettings> Terminals { get; init; } = [];

    /// <summary>Payment mode → provider code (defaults: all = 0, card = 1, upi = 10).</summary>
    public IReadOnlyDictionary<string, string>? PaymentModes { get; init; }

    /// <summary>Status response codes that mean "still waiting for the customer" (default 1001).</summary>
    public IReadOnlyList<int>? PendingResponseCodes { get; init; }

    /// <summary>Simulator only: seconds after which a pushed sale is approved.</summary>
    public int SimulatorApproveAfterSeconds { get; init; } = 8;
}

internal sealed record PosTerminalSettings
{
    /// <summary>HC360 terminal id used by billing counters (letters, digits, '-' or '_').</summary>
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }

    /// <summary>Device identifier registered with the acquirer (Pine Labs "IMEI").</summary>
    public string? Imei { get; init; }

    public string? StorePosCode { get; init; }
}

/// <summary>Secret POS configuration (<c>integrations.pos.secret</c>).</summary>
internal sealed record PosSecrets
{
    public string? SecurityToken { get; init; }
}

internal interface IPosProvider
{
    string Key { get; }

    Task<PosTransactionResult> StartSaleAsync(PosSaleRequest request, PosSettings settings, PosSecrets secrets, CancellationToken cancellationToken);

    Task<PosTransactionResult> GetStatusAsync(string transactionId, PosSettings settings, PosSecrets secrets, CancellationToken cancellationToken);

    Task<PosTransactionResult> CancelAsync(string transactionId, PosSettings settings, PosSecrets secrets, CancellationToken cancellationToken);
}

/// <summary>
/// HC360 transaction ids for cloud POS carry what the status and cancel calls need (terminal, provider reference and
/// amount in paise: <c>OPD-1:123456:10000</c>), so no extra table is required and billing can store them as-is.
/// </summary>
internal static partial class PosTransactionIds
{
    public static string Format(string terminalId, long providerReference, long amountPaise) =>
        string.Create(CultureInfo.InvariantCulture, $"{terminalId}:{providerReference}:{amountPaise}");

    public static bool TryParse(string? transactionId, out string terminalId, out long providerReference, out long amountPaise)
    {
        terminalId = string.Empty;
        providerReference = 0;
        amountPaise = 0;
        var parts = transactionId?.Split(':');
        if (parts is not { Length: 3 } || !TerminalIdPattern().IsMatch(parts[0])
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out providerReference)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out amountPaise))
        {
            return false;
        }

        terminalId = parts[0];
        return true;
    }

    public static bool IsValidTerminalId(string? terminalId) => terminalId is not null && TerminalIdPattern().IsMatch(terminalId);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex TerminalIdPattern();
}
