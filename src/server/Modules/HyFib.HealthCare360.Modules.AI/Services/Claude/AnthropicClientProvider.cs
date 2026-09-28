using System.Security.Cryptography;
using System.Text;
using Anthropic;

namespace HyFib.HealthCare360.Modules.AI.Services.Claude;

/// <summary>Supplies a reusable Anthropic client for the configured key and timeout.</summary>
internal interface IAnthropicClientProvider
{
    IAnthropicClient GetClient(string apiKey, TimeSpan timeout);
}

/// <summary>
/// Keeps one <see cref="AnthropicClient"/> (it pools HTTP connections) and replaces it only when the key or timeout
/// changes. A replaced client is not disposed immediately because requests may still be using it; it is released
/// when the process ends. The key is compared by hash so it is not kept around as a dictionary key.
/// </summary>
internal sealed class AnthropicClientProvider : IAnthropicClientProvider
{
    private readonly Lock _lock = new();
    private string? _fingerprint;
    private AnthropicClient? _client;

    public IAnthropicClient GetClient(string apiKey, TimeSpan timeout)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{apiKey}|{timeout.Ticks}")));
        lock (_lock)
        {
            if (_client is null || _fingerprint != fingerprint)
            {
                // SDK retries (429/5xx/connection errors) stay at their default; the timeout bounds each attempt.
                _client = new AnthropicClient { ApiKey = apiKey, Timeout = timeout, MaxRetries = 2 };
                _fingerprint = fingerprint;
            }

            return _client;
        }
    }
}
