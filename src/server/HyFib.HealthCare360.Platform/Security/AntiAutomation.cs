using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>The kinds of request a proof-of-work challenge is issued for; a challenge is good for its own kind only.</summary>
public static class AntiAutomationActions
{
    /// <summary>The portal's sign-in (and sign-up) with a one-time code: asking for a code, and giving it.</summary>
    public const string Otp = "otp";

    /// <summary>Feedback sent from a QR poster, a kiosk or a personal link.</summary>
    public const string Feedback = "feedback";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Otp, Feedback };
}

/// <summary>
/// The anti-automation settings (Administration, Settings), read hospital-wide. A proof of work is asked for by default;
/// turning it off lets every request through without one.
/// </summary>
public static class AntiAutomationSettings
{
    public const string Enabled = "security.antiAutomation.enabled";

    /// <summary>Leading zero bits asked of every browser.</summary>
    public const string Bits = "security.antiAutomation.bits";

    /// <summary>The most a browser is ever asked for, however suspicious its address or number.</summary>
    public const string MaxBits = "security.antiAutomation.maxBits";

    /// <summary>
    /// 2^14 = 16,384 hashes on average. WebCrypto SHA-256 in a worker does some 20,000-50,000 a second on a mid-range
    /// phone (about 40,000 on a server core), so about 0.3-0.8 s; a bot pays the same for every request.
    /// </summary>
    public const int DefaultBits = 14;

    /// <summary>2^18: sixteen times the default, some 5-13 s on a mid-range phone.</summary>
    public const int DefaultMaxBits = 18;

    /// <summary>The highest either setting may be: 2^24 hashes are minutes on a phone.</summary>
    public const int Ceiling = 24;
}

/// <summary>A challenge for the browser to solve, or <c>Required = false</c> while the check is turned off.</summary>
public sealed record AntiAutomationChallenge(bool Required, string? Challenge, int Bits, DateTimeOffset? ExpiresAt, string Algorithm = "SHA-256");

/// <summary>A challenge for <paramref name="Action"/>; <paramref name="Subject"/> is the mobile number a code is asked for, when known.</summary>
public sealed record AntiAutomationChallengeRequest(string Action, string? Subject);

/// <summary>Endpoint metadata: the endpoint needs a solved challenge for <see cref="Action"/> (<see cref="AntiAutomationEndpoints.RequireProofOfWork"/>).</summary>
public sealed record RequiresProofOfWorkMetadata(string Action);

/// <summary>
/// Failures seen from a client address or for a mobile number in the last 15 minutes: refused proofs, rate-limit hits and
/// refused requests (a wrong code, a throttled number, a dead link). Every 3 failures ask for 2 more bits (four times the
/// work), up to <see cref="AntiAutomationSettings.MaxBits"/>; a quiet quarter of an hour brings it back down. Numbers are
/// kept hashed. In memory, per server.
/// </summary>
public sealed class AutomationSuspicion(IMemoryCache cache)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public const int FailuresPerStep = 3;
    public const int BitsPerStep = 2;

    private readonly Lock _gate = new();

    public void RecordFailure(string? address, string? subject)
    {
        foreach (var key in Keys(address, subject))
        {
            lock (_gate)
            {
                var counter = cache.GetOrCreate(key, entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = Window;
                    return new Counter();
                })!;
                counter.Count++;
            }
        }
    }

    /// <summary>The most failures seen for the address or the number.</summary>
    public int Failures(string? address, string? subject) =>
        Keys(address, subject).Select(key => cache.TryGetValue(key, out Counter? counter) ? counter!.Count : 0).DefaultIfEmpty(0).Max();

    /// <summary>The bits asked of a client: the base, plus <see cref="BitsPerStep"/> for every <see cref="FailuresPerStep"/> failures, at most <paramref name="maxBits"/>.</summary>
    public int RequiredBits(int baseBits, int maxBits, string? address, string? subject) =>
        Math.Max(baseBits, Math.Min(maxBits, baseBits + (BitsPerStep * (Failures(address, subject) / FailuresPerStep))));

    private static IEnumerable<string> Keys(string? address, string? subject)
    {
        if (!string.IsNullOrEmpty(address))
        {
            yield return "pow:fail:ip:" + address;
        }

        if (!string.IsNullOrWhiteSpace(subject))
        {
            var normalized = SharedKernel.Phone.Normalize(subject) ?? subject;
            yield return "pow:fail:subject:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)), 0, 16);
        }
    }

    private sealed class Counter
    {
        public int Count;
    }
}

/// <summary>
/// Issues and checks the proof of work the portal's anonymous endpoints ask for (SRS SEC-06, ASVS V2.4.1). A refused
/// proof gets one generic answer whatever the reason; the reason is counted
/// (<c>hc360.anti_automation.rejected</c>) and audited, at most once per address and reason in 5 minutes so a flood of
/// bad proofs cannot flood the audit trail.
/// </summary>
public sealed class AntiAutomation(
    ProofOfWork proofOfWork,
    AutomationSuspicion suspicion,
    ISettingsService settings,
    IAuditLogger audit,
    IMemoryCache cache,
    TimeProvider time)
{
    public const string ProofHeader = "X-HC360-Proof";
    public const string MeterName = "HyFib.HealthCare360.AntiAutomation";

    /// <summary>Long enough to fill in a form after the browser solved the challenge in the background.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan AuditQuietPeriod = TimeSpan.FromMinutes(5);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Issued = Meter.CreateCounter<long>("hc360.anti_automation.issued", description: "Proof-of-work challenges issued");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("hc360.anti_automation.rejected", description: "Requests refused for a missing or bad proof of work");

    public async Task<AntiAutomationChallenge> IssueAsync(string action, string? subject, string? address, CancellationToken ct)
    {
        var config = await ConfigAsync(ct);
        if (!config.Enabled)
        {
            return new AntiAutomationChallenge(false, null, 0, null);
        }

        var bits = suspicion.RequiredBits(config.Bits, config.MaxBits, address, subject);
        var expiresAt = time.GetUtcNow() + ChallengeLifetime;
        Issued.Add(1, new KeyValuePair<string, object?>("action", action), new KeyValuePair<string, object?>("bits", bits));
        return new AntiAutomationChallenge(true, proofOfWork.Issue(action, bits, expiresAt), bits, expiresAt);
    }

    /// <summary>Checks the proof sent with a request; a refusal is counted against the address and the number.</summary>
    public async Task<ProofOfWorkOutcome> CheckAsync(string? proof, string action, string? subject, string? address, CancellationToken ct)
    {
        var config = await ConfigAsync(ct);
        if (!config.Enabled)
        {
            return ProofOfWorkOutcome.Accepted;
        }

        var outcome = proofOfWork.Verify(proof, action, suspicion.RequiredBits(config.Bits, config.MaxBits, address, subject));
        if (outcome == ProofOfWorkOutcome.Accepted)
        {
            return outcome;
        }

        suspicion.RecordFailure(address, subject);
        var reason = Reason(outcome);
        Rejected.Add(1, new KeyValuePair<string, object?>("action", action), new KeyValuePair<string, object?>("reason", reason));
        if (cache.TryGetValue($"pow:audited:{address}:{reason}", out _))
        {
            return outcome;
        }

        cache.Set($"pow:audited:{address}:{reason}", true, AuditQuietPeriod);
        await audit.WriteAsync(
            new AuditEntry(AuditCategories.Security, "security.anti_automation_rejected", $"Request refused by the anti-automation check ({reason})")
            {
                Details = new { action, reason },
                Severity = AuditSeverity.Warning,
            },
            ct);
        return outcome;
    }

    /// <summary>A request with a good proof was refused anyway (a wrong code, a dead link): the next ones need more work.</summary>
    public void RecordFailure(string? address, string? subject) => suspicion.RecordFailure(address, subject);

    public static string Reason(ProofOfWorkOutcome outcome) => outcome switch
    {
        ProofOfWorkOutcome.Missing => "missing",
        ProofOfWorkOutcome.Malformed => "malformed",
        ProofOfWorkOutcome.Tampered => "tampered",
        ProofOfWorkOutcome.WrongAction => "wrong_action",
        ProofOfWorkOutcome.Expired => "expired",
        ProofOfWorkOutcome.TooEasy => "too_easy",
        ProofOfWorkOutcome.Unsolved => "unsolved",
        ProofOfWorkOutcome.Replayed => "replayed",
        _ => "accepted",
    };

    private async Task<Config> ConfigAsync(CancellationToken ct)
    {
        var enabled = await settings.GetAsync<JsonElement?>(AntiAutomationSettings.Enabled, null, ct) switch
        {
            { ValueKind: JsonValueKind.False } => false,
            { ValueKind: JsonValueKind.String } text when bool.TryParse(text.GetString(), out var on) => on,
            _ => true,
        };
        var bits = Math.Clamp(await IntAsync(AntiAutomationSettings.Bits, AntiAutomationSettings.DefaultBits, ct), 0, AntiAutomationSettings.Ceiling);
        var maxBits = Math.Clamp(await IntAsync(AntiAutomationSettings.MaxBits, AntiAutomationSettings.DefaultMaxBits, ct), bits, AntiAutomationSettings.Ceiling);
        return new Config(enabled, bits, maxBits);
    }

    private async Task<int> IntAsync(string key, int fallback, CancellationToken ct) =>
        await settings.GetAsync<JsonElement?>(key, null, ct) switch
        {
            { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var n) => n,
            { ValueKind: JsonValueKind.String } text when int.TryParse(text.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => fallback,
        };

    private sealed record Config(bool Enabled, int Bits, int MaxBits);
}

/// <summary>The challenge endpoint, and <see cref="RequireProofOfWork"/> for the endpoints that ask for a solved one.</summary>
public static class AntiAutomationEndpoints
{
    public const string FailedCode = "request.check_failed";

    /// <summary>The one answer to every refused proof, whatever the reason.</summary>
    public static IResult Failed() => new Error(
        FailedCode,
        "We could not complete this request. Please try again.",
        ErrorType.Forbidden).ToProblem();

    public static void Map(IEndpointRouteBuilder app)
    {
        // Under /api/v1/public, so the portal's address and the staff app's both serve it (feedback kiosks use either).
        app.MapPost("/api/v1/public/challenge", IssueAsync)
            .WithTags("Public")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public);
    }

    /// <summary>
    /// The endpoint answers only with a solved challenge for <paramref name="action"/> in <see cref="AntiAutomation.ProofHeader"/>,
    /// and a 403 <see cref="FailedCode"/> otherwise, before its handler runs. <paramref name="subject"/> picks the mobile
    /// number out of the request, when it has one, so failures for one number raise the work asked for it from any
    /// address. A 4xx answer from the handler (a wrong code, a dead link) counts as a failure too.
    /// </summary>
    public static TBuilder RequireProofOfWork<TBuilder>(this TBuilder builder, string action, Func<EndpointFilterInvocationContext, string?>? subject = null)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!AntiAutomationActions.All.Contains(action))
        {
            throw new ArgumentException($"Unknown anti-automation action '{action}'.", nameof(action));
        }

        builder.WithMetadata(new RequiresProofOfWorkMetadata(action));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var check = http.RequestServices.GetRequiredService<AntiAutomation>();
            var who = subject?.Invoke(context);
            var address = http.Connection.RemoteIpAddress?.ToString();
            var outcome = await check.CheckAsync(http.Request.Headers[AntiAutomation.ProofHeader].ToString(), action, who, address, http.RequestAborted);
            if (outcome != ProofOfWorkOutcome.Accepted)
            {
                return Failed();
            }

            var result = await next(context);
            if (result is IStatusCodeHttpResult { StatusCode: >= 400 and < 500 })
            {
                check.RecordFailure(address, who);
            }

            return result;
        });
        return builder;
    }

    /// <summary>A rate-limit refusal on an endpoint that asks for a proof raises the work asked of the address.</summary>
    internal static void OnRateLimited(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<RequiresProofOfWorkMetadata>() is not null)
        {
            http.RequestServices.GetRequiredService<AutomationSuspicion>().RecordFailure(http.Connection.RemoteIpAddress?.ToString(), null);
        }
    }

    private static async Task<Results<Ok<AntiAutomationChallenge>, ValidationProblem>> IssueAsync(
        AntiAutomationChallengeRequest request,
        AntiAutomation antiAutomation,
        HttpContext http,
        CancellationToken ct)
    {
        if (request.Action is null || !AntiAutomationActions.All.Contains(request.Action) || request.Subject is { Length: > 32 })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["action"] = ["Unknown request."] });
        }

        return TypedResults.Ok(await antiAutomation.IssueAsync(request.Action, request.Subject, http.Connection.RemoteIpAddress?.ToString(), ct));
    }
}
