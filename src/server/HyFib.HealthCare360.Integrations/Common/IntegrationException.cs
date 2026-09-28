using Polly.CircuitBreaker;
using Polly.Timeout;

namespace HyFib.HealthCare360.Integrations.Common;

/// <summary>
/// Thrown by adapters whose platform contract has no failure channel (for example
/// <see cref="IPaymentGateway.CreateOrderAsync"/> returns an order, not a result). It derives from
/// <see cref="InvalidOperationException"/> so modules that do not reference this project can still catch it, and its
/// message is safe to show to staff: it never contains credentials or raw provider payloads.
/// </summary>
public sealed class IntegrationException : InvalidOperationException
{
    public IntegrationException()
    {
    }

    public IntegrationException(string message)
        : base(message)
    {
    }

    public IntegrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Staff-facing descriptions of transport failures (no stack traces, no credentials, no payloads).</summary>
internal static class IntegrationErrors
{
    public static string Describe(Exception exception, string provider) => exception switch
    {
        TimeoutRejectedException or TaskCanceledException or TimeoutException => $"{provider} did not respond in time.",
        BrokenCircuitException => $"{provider} is temporarily unavailable after repeated failures; it will be retried shortly.",
        HttpRequestException { StatusCode: { } status } => $"{provider} returned HTTP {(int)status}.",
        HttpRequestException => $"Could not reach {provider}. Check the internet connection and the configured address.",
        JsonException => $"{provider} returned a response HC360 could not read.",
        IntegrationException integration => integration.Message,
        _ => $"The {provider} call failed ({exception.GetType().Name}).",
    };

    /// <summary>True for failures of the remote call itself — not cancellation requested by our own caller.</summary>
    public static bool IsCallFailure(Exception exception, CancellationToken callerToken) =>
        exception is not OperationCanceledException || !callerToken.IsCancellationRequested;
}
