using System.Collections.Concurrent;
using System.Net.Sockets;

namespace HyFib.HealthCare360.Platform.Messaging;

/// <summary>
/// Where an outbox handler's delivery goes (<see cref="IOutboxHandler.Lane"/>). It decides what can hold the delivery up:
/// an internet outage holds up only the internet lane, and so does the hold after a restore (<see cref="OutboxHold"/>).
/// </summary>
public enum OutboxLane
{
    /// <summary>To a service on the internet: SMS, WhatsApp, e-mail, ABDM, e-invoicing. The default.</summary>
    Internet = 0,

    /// <summary>
    /// Inside the hospital: in-app and realtime notifications, work in the database, and systems on the LAN (Tally, the
    /// analysers' order queue). A handler opts in, and must then call nothing on the internet.
    /// </summary>
    Local,
}

/// <summary>
/// Thrown by an outbox handler when its provider could not be reached or did not answer in time, rather than answering
/// with an error of its own: the message is tried again as any failure is, and the failure counts towards the provider's
/// cool-down (<see cref="OutboxCoolDowns"/>). It derives from <see cref="InvalidOperationException"/>, as the failures
/// handlers threw before it.
/// </summary>
public sealed class ProviderUnreachableException : InvalidOperationException
{
    public ProviderUnreachableException()
    {
    }

    public ProviderUnreachableException(string message)
        : base(message)
    {
    }

    public ProviderUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Tells a failed delivery whose provider could not be reached from one the provider answered.</summary>
internal static class OutboxFailures
{
    /// <summary>
    /// True when the delivery failed for want of an answer: a <see cref="ProviderUnreachableException"/>, a connection that
    /// could not be made or broke, or a timeout (the dispatcher tells its own stop apart before asking). False for an
    /// answer, such as a provider's refusal, and for a failure of the message's own.
    /// </summary>
    public static bool IsUnreachable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ProviderUnreachableException or TimeoutException or SocketException or OperationCanceledException:
                    return true;
                case HttpRequestException { StatusCode: null, HttpRequestError: var error } when NoAnswer(error):
                    return true;
                case AggregateException aggregate:
                    return aggregate.InnerExceptions.Any(IsUnreachable);
            }
        }

        return false;
    }

    // A request HC360 refused itself (an HttpRequestException made by hand) has no error kind, and is not counted.
    private static bool NoAnswer(HttpRequestError error) => error is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
        or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError or HttpRequestError.ResponseEnded;
}

/// <summary>
/// Each provider's run of failed deliveries that got no answer (<see cref="OutboxFailures.IsUnreachable"/>). After
/// <see cref="FailuresBeforeCoolDown"/> in a row its messages wait out a cool-down, <see cref="FirstCoolDown"/> doubling
/// to <see cref="LongestCoolDown"/>, without the dispatcher spending any time on them, and the other providers carry on.
/// The first message tried after a cool-down shows whether the provider answers again: it starts the next, longer cool-down
/// if not. An answer of any kind, or a failure of the message's own, ends the run. Kept in memory: a restart starts
/// afresh, and each server of several keeps its own.
/// </summary>
internal sealed class OutboxCoolDowns
{
    public const int FailuresBeforeCoolDown = 3;

    public static readonly TimeSpan FirstCoolDown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest cool-down: also the longest a provider's messages wait after the internet comes back. Short, as the
    /// one message tried after each costs only its own provider's time.
    /// </summary>
    public static readonly TimeSpan LongestCoolDown = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);

    /// <summary>When the provider's cool-down ends; null when it is not cooling down at <paramref name="now"/>.</summary>
    public DateTimeOffset? CoolingUntil(string provider, DateTimeOffset now) =>
        _runs.TryGetValue(provider, out var run) && run.Until > now ? run.Until : null;

    /// <summary>The provider answered, or the delivery failed for a reason of its own: its run of failures ends.</summary>
    public void Answered(string provider) => _runs.TryRemove(provider, out _);

    /// <summary>A delivery to the provider got no answer. Returns when the cool-down this failure starts ends, if it starts one.</summary>
    public DateTimeOffset? Unreachable(string provider, DateTimeOffset now)
    {
        var run = _runs.AddOrUpdate(provider, _ => Next(new Run(0, 0, DateTimeOffset.MinValue), now), (_, previous) => Next(previous, now));
        return run.Until > now ? run.Until : null;
    }

    private static Run Next(Run run, DateTimeOffset now)
    {
        var failures = run.Failures + 1;
        if (failures < FailuresBeforeCoolDown)
        {
            return run with { Failures = failures };
        }

        var wait = TimeSpan.FromTicks(Math.Min(FirstCoolDown.Ticks * (1L << Math.Min(run.CoolDowns, 16)), LongestCoolDown.Ticks));
        return new Run(failures, run.CoolDowns + 1, now + wait);
    }

    /// <param name="CoolDowns">How many cool-downs this run has started; each lasts twice the one before, up to the longest.</param>
    private sealed record Run(int Failures, int CoolDowns, DateTimeOffset Until);
}
