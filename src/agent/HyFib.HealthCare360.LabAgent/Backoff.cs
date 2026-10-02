using System.Security.Cryptography;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>
/// Waits that double after each failure up to a ceiling, with up to a fifth of random spread so several agents that lost
/// the server at the same moment do not all come back at the same moment.
/// </summary>
internal sealed class Backoff(TimeSpan first, TimeSpan ceiling)
{
    private int _failures;

    public TimeSpan Next()
    {
        var exponent = Math.Min(_failures, 16);
        _failures++;
        var delay = TimeSpan.FromMilliseconds(Math.Min(first.TotalMilliseconds * Math.Pow(2, exponent), ceiling.TotalMilliseconds));
        var spread = (int)Math.Max(1, delay.TotalMilliseconds / 5);
        return delay - TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, spread));
    }

    public void Reset() => _failures = 0;
}
