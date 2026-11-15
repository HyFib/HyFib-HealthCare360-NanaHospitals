using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The clock of a test server whose day the tests turn (<see cref="MidnightInIndiaServer"/>): every "now" the server reads.
/// It runs as the real clock does, from the time a test last set it to, so the server's work takes the time it takes and a
/// rate limit's minute still ends; and it only goes forward, as the hospital's does. Back over midnight, a number series
/// that started again at midnight would start again a second time and issue a number twice. Timers keep the real clock's
/// pace (the base class makes them), so a job that runs every hour still waits an hour.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private readonly Lock _gate = new();

    // How far this clock is ahead of the real one (behind, when negative).
    private TimeSpan _ahead;

    public TestClock(DateTimeOffset start) => _ahead = start - TimeProvider.System.GetUtcNow();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return TimeProvider.System.GetUtcNow() + _ahead;
        }
    }

    /// <summary>Sets the clock to <paramref name="instant"/>, from where it runs on. Never back: see the class.</summary>
    public void MoveTo(DateTimeOffset instant)
    {
        lock (_gate)
        {
            var real = TimeProvider.System.GetUtcNow();
            if (instant < real + _ahead)
            {
                throw new InvalidOperationException($"The test clock only goes forward: it is {real + _ahead:O}, past {instant:O}.");
            }

            _ahead = instant - real;
        }
    }

    /// <summary>
    /// Whether a sign-in's token is within its lifetime on this clock, for the token handler
    /// (<see cref="TokenValidationParameters.LifetimeValidator"/>). The server dates its tokens by this clock, and the handler
    /// would otherwise check them against the real one, where a token made at 23:59 on a day ahead is not yet valid.
    /// </summary>
    public bool IsWithinLifetime(DateTime? notBefore, DateTime? expires, SecurityToken token, TokenValidationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var now = GetUtcNow().UtcDateTime;
        return (notBefore is not { } from || from - parameters.ClockSkew <= now)
               && (expires is not { } until || now < until + parameters.ClockSkew);
    }
}

/// <summary>
/// A test class whose tests keep the hospital's time on a clock of their own (<see cref="TestClock"/>), not on the real one:
/// none waits for midnight in India to pass (<see cref="AwayFromMidnightInIndiaAttribute"/>), since the real midnight is not
/// theirs. They cross the one they choose.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class OnTestClockAttribute : Attribute;
