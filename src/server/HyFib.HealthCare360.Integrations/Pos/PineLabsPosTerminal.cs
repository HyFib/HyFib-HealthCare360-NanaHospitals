using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Pos;

/// <summary>
/// Pine Labs Plutus Cloud-style adapter: upload the billed transaction to a terminal, poll its status, cancel it.
/// The security token travels in the body, so request bodies are never logged — only terminal, amount and reference.
/// </summary>
internal sealed class PineLabsPosTerminal(HttpClient http, IntegrationTrace trace) : IPosProvider
{
    private static readonly Dictionary<string, string> DefaultModes = new(StringComparer.OrdinalIgnoreCase) { ["all"] = "0", ["card"] = "1", ["upi"] = "10" };

    public string Key => ProviderKeys.PineLabs;

    public async Task<PosTransactionResult> StartSaleAsync(PosSaleRequest request, PosSettings settings, PosSecrets secrets, CancellationToken cancellationToken)
    {
        var terminal = settings.Terminals.FirstOrDefault(t => string.Equals(t.Id, request.TerminalId, StringComparison.OrdinalIgnoreCase));
        if (terminal is null || !PosTransactionIds.IsValidTerminalId(terminal.Id))
        {
            return PosTerminalRouter.Declined(string.Empty, $"Terminal '{request.TerminalId}' is not configured.");
        }

        long paise;
        try
        {
            paise = Payments.PaymentAmounts.ToPaise(request.Amount);
        }
        catch (IntegrationException ex)
        {
            return PosTerminalRouter.Declined(string.Empty, ex.Message);
        }

        var body = Credentials(settings, secrets, terminal);
        body["TransactionNumber"] = IntegrationTrace.Truncate(request.Reference, 30);
        body["SequenceNumber"] = 1;
        body["AllowedPaymentMode"] = PaymentMode(request.Mode, settings);
        body["Amount"] = paise;
        body["UserID"] = settings.UserId;
        body["AutoCancelDurationInMinutes"] = Math.Clamp(settings.AutoCancelMinutes, 1, 30);

        var response = await CallAsync(settings, settings.UploadPath, body, "sale.upload", request.Reference,
            $"{{\"terminal\":\"{terminal.Id}\",\"amount\":{paise}}}", cancellationToken);
        if (response.Result is { } failed)
        {
            return failed;
        }

        return response.Response!.ResponseCode == 0
            ? new PosTransactionResult(PosTransactionIds.Format(terminal.Id, response.Response.ReferenceId, paise), PosTransactionStatus.AwaitingCustomer, null, null, null, response.Response.ResponseMessage)
            : PosTerminalRouter.Declined(string.Empty, response.Response.ResponseMessage ?? $"Terminal response {response.Response.ResponseCode}");
    }

    public async Task<PosTransactionResult> GetStatusAsync(string transactionId, PosSettings settings, PosSecrets secrets, CancellationToken cancellationToken)
    {
        if (!TryTerminal(transactionId, settings, out var terminal, out var reference, out _))
        {
            return PosTerminalRouter.Declined(transactionId, "Unknown POS transaction.");
        }

        var body = Credentials(settings, secrets, terminal);
        body["PlutusTransactionReferenceID"] = reference;
        var response = await CallAsync(settings, settings.StatusPath, body, "sale.status", transactionId, null, cancellationToken);
        return response.Result ?? PineLabsResponses.MapStatus(transactionId, response.Response!, settings.PendingResponseCodes ?? [PineLabsResponses.Pending]);
    }

    public async Task<PosTransactionResult> CancelAsync(string transactionId, PosSettings settings, PosSecrets secrets, CancellationToken cancellationToken)
    {
        if (!TryTerminal(transactionId, settings, out var terminal, out var reference, out var paise))
        {
            return PosTerminalRouter.Declined(transactionId, "Unknown POS transaction.");
        }

        var body = Credentials(settings, secrets, terminal);
        body["PlutusTransactionReferenceID"] = reference;
        body["Amount"] = paise;
        var response = await CallAsync(settings, settings.CancelPath, body, "sale.cancel", transactionId, null, cancellationToken);
        if (response.Result is { } failed)
        {
            return failed;
        }

        return response.Response!.ResponseCode == 0
            ? new PosTransactionResult(transactionId, PosTransactionStatus.Cancelled, null, null, null, response.Response.ResponseMessage)
            : PineLabsResponses.MapStatus(transactionId, response.Response, settings.PendingResponseCodes ?? [PineLabsResponses.Pending]);
    }

    internal static string PaymentMode(string? mode, PosSettings settings)
    {
        var key = string.IsNullOrWhiteSpace(mode) ? "all" : mode.Trim();
        return settings.PaymentModes?.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)).Value
               ?? DefaultModes.GetValueOrDefault(key)
               ?? DefaultModes["all"];
    }

    private static Dictionary<string, object?> Credentials(PosSettings settings, PosSecrets secrets, PosTerminalSettings terminal) => new(StringComparer.Ordinal)
    {
        // Plutus expects a numeric merchant id; keep the configured text when it is not numeric.
        ["MerchantID"] = int.TryParse(settings.MerchantId, NumberStyles.None, CultureInfo.InvariantCulture, out var merchant) ? merchant : settings.MerchantId,
        ["SecurityToken"] = secrets.SecurityToken,
        ["IMEI"] = terminal.Imei,
        ["MerchantStorePosCode"] = terminal.StorePosCode,
    };

    private static bool TryTerminal(string transactionId, PosSettings settings, out PosTerminalSettings terminal, out long reference, out long paise)
    {
        terminal = new PosTerminalSettings();
        if (!PosTransactionIds.TryParse(transactionId, out var terminalId, out reference, out paise))
        {
            return false;
        }

        var match = settings.Terminals.FirstOrDefault(t => string.Equals(t.Id, terminalId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        terminal = match;
        return true;
    }

    private async Task<(PineLabsResponse? Response, PosTransactionResult? Result)> CallAsync(
        PosSettings settings, string path, Dictionary<string, object?> body, string operation, string? reference, string? summary, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (!IntegrationHttp.TryCreateEndpoint(settings.BaseUrl, requireHttps: true, out var baseUri))
        {
            const string invalid = "The POS base URL must be an https address.";
            await trace.LogAsync(IntegrationNames.Pos, Key, operation, false, started, reference, summary, error: invalid);
            return (null, PosTerminalRouter.Declined(reference ?? string.Empty, invalid));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, IntegrationHttp.Combine(baseUri, path)) { Content = IntegrationHttp.JsonBody(body) };
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = response.IsSuccessStatusCode ? PineLabsResponses.Parse(text) : null;
            var error = parsed is null ? $"The POS service returned HTTP {(int)response.StatusCode} without a readable response." : null;
            await trace.LogAsync(IntegrationNames.Pos, Key, operation, parsed is not null, started, reference, summary,
                parsed is null ? $"HTTP {(int)response.StatusCode}" : $"code={parsed.ResponseCode} message={parsed.ResponseMessage}", error);
            return parsed is null ? (null, PosTerminalRouter.Declined(reference ?? string.Empty, error)) : (parsed, null);
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "The POS service");
            await trace.LogAsync(IntegrationNames.Pos, Key, operation, false, started, reference, summary, error: error);
            // A lost status/cancel response is not a decline: report "still waiting" so the counter polls again.
            var status = operation == "sale.upload" ? PosTransactionStatus.Declined : PosTransactionStatus.AwaitingCustomer;
            return (null, new PosTransactionResult(operation == "sale.upload" ? string.Empty : reference ?? string.Empty, status, null, null, null, error));
        }
    }
}
