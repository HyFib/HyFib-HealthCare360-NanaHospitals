namespace HyFib.HealthCare360.Integrations.Common;

/// <summary>
/// Writes one integration-log row per external call. Callers pass summaries they built themselves — recipient masked,
/// no message bodies, never credentials — because provider responses often echo PHI (Twilio returns the SMS text,
/// Meta the phone number). The platform logger then applies its own phone/e-mail/secret masking as a second line of
/// defence (SEC-10).
/// </summary>
internal sealed class IntegrationTrace(IIntegrationLogger logger, TimeProvider time)
{
    public long Start() => time.GetTimestamp();

    public Task LogAsync(
        string integration,
        string provider,
        string operation,
        bool success,
        long started,
        string? reference = null,
        string? request = null,
        string? response = null,
        string? error = null,
        IntegrationDirection direction = IntegrationDirection.Outbound) =>
        // CancellationToken.None: the trace must be written even when the caller gave up — that is when it matters most.
        logger.LogAsync(
            new IntegrationLogEntry(integration, provider, operation, direction, success, time.GetElapsedTime(started),
                Truncate(reference, 128), request, response, Truncate(error, 2000)),
            CancellationToken.None);

    public static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
