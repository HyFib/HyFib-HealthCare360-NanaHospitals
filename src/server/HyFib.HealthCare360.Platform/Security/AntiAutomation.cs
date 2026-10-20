using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// 2^14 = 16,384 hashes on average: WebCrypto SHA-256 in a worker does some 20,000-50,000 a second on a mid-range
    /// phone, so about 0.3-0.8 s. It is no wall: a native SHA-256 solver does 2^14-2^18 hashes in milliseconds. The proof
    /// raises the cost of cheap, high-volume scripting (a fresh challenge and its own work for every request); the rate
    /// limits per address and per number stay the main control.
    /// </summary>
    public const int DefaultBits = 14;

    /// <summary>2^18: sixteen times the default, some 5-13 s on a mid-range phone.</summary>
    public const int DefaultMaxBits = 18;

    /// <summary>
    /// The highest either setting may be: 2^20 hashes, some 20-50 s on a mid-range phone on average. A challenge lives
    /// 5 minutes (<see cref="AntiAutomation.ChallengeLifetime"/>) and the tries a solution takes vary (geometrically): a
    /// slow phone needs three times the average one time in twenty, some 2.5 minutes, still within it. At 2^22 the
    /// average alone is 1.5-3.5 minutes, and at 2^24 six to fourteen: patients' phones would run out of time.
    /// </summary>
    public const int Ceiling = 20;

    /// <summary>
    /// Why <paramref name="value"/> cannot be saved as <paramref name="key"/>, or null (and for any other key): the
    /// bits are a whole number from 0 to <see cref="Ceiling"/> (a number or its text, as they are read), the switch true or
    /// false. Clearing a value (null) is fine: the default applies.
    /// </summary>
    public static string? Problem(string key, JsonNode? value) => key switch
    {
        _ when value is null => null,
        Bits or MaxBits => IntOf(value) is >= 0 and <= Ceiling ? null : $"Enter a whole number of bits from 0 to {Ceiling}.",
        Enabled => BoolOf(value) is not null ? null : "Enter true or false.",
        _ => null,
    };

    private static int? IntOf(JsonNode value) => value is JsonValue v
        ? v.TryGetValue<int>(out var n) ? n
            : v.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : null
        : null;

    private static bool? BoolOf(JsonNode value) => value is JsonValue v
        ? v.TryGetValue<bool>(out var on) ? on
            : v.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) ? parsed
            : null
        : null;
}

/// <summary>A challenge for the browser to solve, or <c>Required = false</c> while the check is turned off.</summary>
public sealed record AntiAutomationChallenge(bool Required, string? Challenge, int Bits, DateTimeOffset? ExpiresAt, string Algorithm = "SHA-256");

/// <summary>A challenge for <paramref name="Action"/>; <paramref name="Subject"/> is the mobile number a code is asked for, when known.</summary>
public sealed record AntiAutomationChallengeRequest(string Action, string? Subject);

/// <summary>Endpoint metadata: the endpoint needs a solved challenge for <see cref="Action"/> (<see cref="AntiAutomationEndpoints.RequireProofOfWork"/>).</summary>
public sealed record RequiresProofOfWorkMetadata(string Action);

/// <summary>
/// Failures from a client address or for a mobile number: refused proofs, rate-limit hits and refused requests (a wrong
/// code, a throttled number, a dead link). Every 3 failures ask for 2 more bits (four times the work), up to
/// <see cref="AntiAutomationSettings.MaxBits"/>. The count is kept until <see cref="Window"/> passes without a failure (each
/// failure starts the quarter of an hour again), so a quiet quarter of an hour brings it back down and a steady trickle
/// keeps it up. In memory, per server. Numbers are kept only as an HMAC-SHA256 under a key derived from the server's
/// anti-automation key (<see cref="ProofOfWorkKey"/>): India has few enough mobile numbers (some 10^9) to hash every one,
/// so a plain hash would give the number back to whoever saw the memory.
/// </summary>
public sealed class AutomationSuspicion(IMemoryCache cache, TimeProvider time, byte[] key)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public const int FailuresPerStep = 3;
    public const int BitsPerStep = 2;

    private readonly Lock _gate = new();
    private readonly byte[] _subjectKey = FieldMasterKey.DeriveKey(key, "anti-automation-subject");

    public void RecordFailure(string? address, string? subject)
    {
        var now = time.GetUtcNow();
        foreach (var key in Keys(address, subject))
        {
            lock (_gate)
            {
                var counter = Current(key, now) ?? new Counter();
                counter.Count++;
                counter.LastFailureAt = now;

                // Kept a quarter of an hour from this failure, not from the first. The cache's own expiry only frees the
                // memory; the window is judged on this server's TimeProvider (Current).
                cache.Set(key, counter, Window);
            }
        }
    }

    /// <summary>The most failures counted for the address or the number.</summary>
    public int Failures(string? address, string? subject)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            return Keys(address, subject).Select(key => Current(key, now)?.Count ?? 0).DefaultIfEmpty(0).Max();
        }
    }

    /// <summary>The bits asked of a client: the base, plus <see cref="BitsPerStep"/> for every <see cref="FailuresPerStep"/> failures, at most <paramref name="maxBits"/>.</summary>
    public int RequiredBits(int baseBits, int maxBits, string? address, string? subject) =>
        Math.Max(baseBits, Math.Min(maxBits, baseBits + (BitsPerStep * (Failures(address, subject) / FailuresPerStep))));

    private IEnumerable<string> Keys(string? address, string? subject)
    {
        if (!string.IsNullOrEmpty(address))
        {
            yield return "pow:fail:ip:" + address;
        }

        if (!string.IsNullOrWhiteSpace(subject))
        {
            yield return "pow:fail:subject:" + SubjectHash(subject);
        }
    }

    /// <summary>What the count for a mobile number is kept under: the keyed hash of the number, written as HC360 stores numbers.</summary>
    internal string SubjectHash(string subject)
    {
        var normalized = SharedKernel.Phone.Normalize(subject) ?? subject;
        return Convert.ToHexString(HMACSHA256.HashData(_subjectKey, Encoding.UTF8.GetBytes(normalized)), 0, 16);
    }

    private Counter? Current(string key, DateTimeOffset now) =>
        cache.TryGetValue(key, out Counter? counter) && now - counter!.LastFailureAt < Window ? counter : null;

    private sealed class Counter
    {
        public int Count;
        public DateTimeOffset LastFailureAt;
    }
}

/// <summary>
/// Issues and checks the proof of work the portal's anonymous endpoints ask for (SRS SEC-06, ASVS V2.4.1). A refused
/// proof gets one generic answer whatever the reason; the reason is counted
/// (<c>hc360.anti_automation.rejected</c>) and, unless it is <see cref="ProofOfWorkOutcome.TooEasy"/>
/// (<see cref="IsFailure"/>), audited, at most once per address and reason in 5 minutes so a flood of bad proofs cannot
/// flood the audit trail.
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

    /// <summary>
    /// Checks the proof sent with a request; a refusal that <see cref="IsFailure">is a failure</see> is counted against the
    /// address and the number.
    /// </summary>
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

        var reason = Reason(outcome);
        Rejected.Add(1, new KeyValuePair<string, object?>("action", action), new KeyValuePair<string, object?>("reason", reason));
        if (!IsFailure(outcome))
        {
            return outcome;
        }

        suspicion.RecordFailure(address, subject);
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

    /// <summary>
    /// Whether a refused proof counts against the address and the number. A too-easy one does not, and is not audited: it
    /// was solved, for a challenge issued before the work asked of them went up (the portal solves its sign-in challenge
    /// before the number is typed), and the browser then solves a harder one. Counting it would raise the work further on
    /// every try of a patient whose number is already under suspicion.
    /// </summary>
    public static bool IsFailure(ProofOfWorkOutcome outcome) => outcome is not (ProofOfWorkOutcome.Accepted or ProofOfWorkOutcome.TooEasy);

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

    /// <summary>What a refused proof is told, which the portal shows in the patient's language (patient texts, PP-06).</summary>
    public const string FailedMessage = "We could not complete this request. Please try again.";

    /// <summary>The one answer to every refused proof, whatever the reason.</summary>
    public static IResult Failed() => new Error(FailedCode, FailedMessage, ErrorType.Forbidden).ToProblem();

    public static void Map(IEndpointRouteBuilder app)
    {
        // Under /api/v1/public, so the portal's address and the staff app's both serve it (feedback kiosks use either). Its
        // bits for a number show whether that number has failed lately: an accepted disclosure (SECURITY.md).
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
    /// <para>
    /// <paramref name="subjectOnFailure"/> is for a request that names the number only through a record (giving a sign-in
    /// code names its challenge): the number is looked up only when the request fails, and the failure counts against it
    /// too. The work asked for such a request depends on the address alone.
    /// </para>
    /// <para>
    /// Put it after <c>WithValidation</c>: a request that fails validation then never reaches the check, so it neither
    /// spends its proof nor counts as a failure.
    /// </para>
    /// </summary>
    public static TBuilder RequireProofOfWork<TBuilder>(
        this TBuilder builder,
        string action,
        Func<EndpointFilterInvocationContext, string?>? subject = null,
        Func<EndpointFilterInvocationContext, CancellationToken, Task<string?>>? subjectOnFailure = null)
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
                // CheckAsync counted it against the address, and against the number when the request names it.
                if (who is null && subjectOnFailure is not null && AntiAutomation.IsFailure(outcome))
                {
                    check.RecordFailure(null, await subjectOnFailure(context, http.RequestAborted));
                }

                return Failed();
            }

            var result = await next(context);
            if (result is IStatusCodeHttpResult { StatusCode: >= 400 and < 500 })
            {
                check.RecordFailure(address, who ?? (subjectOnFailure is null ? null : await subjectOnFailure(context, http.RequestAborted)));
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
