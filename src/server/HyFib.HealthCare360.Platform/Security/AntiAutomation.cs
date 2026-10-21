using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// keeps it up.
/// <para>
/// The counts are kept in <paramref name="store"/>: on a node of several the cluster store, so failures counted by one
/// node raise the work every node asks for (SCAL-01), and on a single Server Node its memory
/// (<see cref="MemoryClusterStore"/>). Addresses and numbers are kept only as an HMAC-SHA256 under keys derived from the
/// anti-automation key (<see cref="ProofOfWorkKey"/>): every IPv4 address, and every one of India's mobile numbers (some
/// 10^9), can be hashed in minutes, so a plain hash would give them back to whoever read the store or a backup of it.
/// </para>
/// </summary>
public sealed class AutomationSuspicion(IClusterStore store, IMemoryCache cache, TimeProvider time, byte[] key, ILogger<AutomationSuspicion> logger)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public const int FailuresPerStep = 3;
    public const int BitsPerStep = 2;

    /// <summary>
    /// A count this high asks for the most work any settings can ask for (<see cref="AntiAutomationSettings.Ceiling"/> bits
    /// over none): more failures cannot raise it.
    /// </summary>
    public const int SaturatedFailures = FailuresPerStep * ((AntiAutomationSettings.Ceiling + BitsPerStep - 1) / BitsPerStep);

    /// <summary>
    /// How often a node counts a failure for a count already <see cref="SaturatedFailures"/>: often enough to keep its quiet
    /// quarter of an hour from running out while the failures go on.
    /// </summary>
    public static readonly TimeSpan SaturatedInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long one refusal for an address and a reason keeps the next ones out of the audit trail.</summary>
    public static readonly TimeSpan AuditQuietPeriod = TimeSpan.FromMinutes(5);

    private const string LocalPrefix = "anti-automation:";

    private readonly byte[] _subjectKey = FieldMasterKey.DeriveKey(key, "anti-automation-subject");
    private readonly byte[] _addressKey = FieldMasterKey.DeriveKey(key, "anti-automation-address");

    /// <summary>
    /// Counts a failure against the address and the number, whether or not the client is still there: a script that sends
    /// a bad proof and hangs up at once is counted as one that waits for the answer. Past <see cref="SaturatedFailures"/>,
    /// this node counts one a minute (<see cref="SaturatedInterval"/>): rate-limit refusals come as fast as a script sends
    /// them, and on a node of several each failure counted is a statement on the database the wards use. The answer the
    /// client gets does not hang on the count: a count the store cannot take is logged, and this node tries again for that
    /// address or number a minute later, so a store that is down is not asked once per refusal.
    /// </summary>
    public async Task RecordFailureAsync(string? address, string? subject)
    {
        var now = time.GetUtcNow();
        foreach (var key in Keys(address, subject))
        {
            if (cache.TryGetValue(LocalPrefix + key, out DateTimeOffset quietUntil) && now < quietUntil)
            {
                continue;
            }

            try
            {
                if ((await store.IncrementUntilQuietAsync(key, Window, CancellationToken.None)).Count < SaturatedFailures)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Counting a failed request towards the anti-automation check failed; this server tries again in a minute");
            }

            cache.Set(LocalPrefix + key, now + SaturatedInterval, SaturatedInterval);
        }
    }

    /// <summary>The most failures counted for the address or the number.</summary>
    public async Task<long> FailuresAsync(string? address, string? subject, CancellationToken ct)
    {
        var most = 0L;
        foreach (var key in Keys(address, subject))
        {
            most = Math.Max(most, await store.GetCountAsync(key, ct));
        }

        return most;
    }

    /// <summary>The bits asked of a client: the base, plus <see cref="BitsPerStep"/> for every <see cref="FailuresPerStep"/> failures, at most <paramref name="maxBits"/>.</summary>
    public async Task<int> RequiredBitsAsync(int baseBits, int maxBits, string? address, string? subject, CancellationToken ct)
    {
        // A count far past what can raise the work (a long flood) must not overflow the sum.
        var steps = (int)Math.Min(await FailuresAsync(address, subject, ct) / FailuresPerStep, AntiAutomationSettings.Ceiling);
        return Math.Max(baseBits, Math.Min(maxBits, baseBits + (BitsPerStep * steps)));
    }

    /// <summary>
    /// Whether a refusal for <paramref name="reason"/> from <paramref name="address"/> is the first in
    /// <see cref="AuditQuietPeriod"/> on any server: only that one is audited, so a flood of bad proofs cannot flood the
    /// audit trail. Once this node knows of one, it asks the store no more until the period is over.
    /// </summary>
    public async Task<bool> IsFirstRefusalAsync(string? address, string reason, CancellationToken ct)
    {
        var key = $"pow:audited:{reason}:" + (string.IsNullOrEmpty(address) ? "-" : AddressHash(address));
        var now = time.GetUtcNow();
        if (cache.TryGetValue(LocalPrefix + key, out DateTimeOffset quietUntil) && now < quietUntil)
        {
            return false;
        }

        cache.Set(LocalPrefix + key, now + AuditQuietPeriod, AuditQuietPeriod);
        return await store.TryAddAsync(key, AuditQuietPeriod, ct);
    }

    private IEnumerable<string> Keys(string? address, string? subject)
    {
        if (!string.IsNullOrEmpty(address))
        {
            yield return "pow:fail:ip:" + AddressHash(address);
        }

        if (!string.IsNullOrWhiteSpace(subject))
        {
            yield return "pow:fail:subject:" + SubjectHash(subject);
        }
    }

    /// <summary>What the count for a client address is kept under: the keyed hash of the address.</summary>
    internal string AddressHash(string address) =>
        Convert.ToHexString(HMACSHA256.HashData(_addressKey, Encoding.UTF8.GetBytes(address)), 0, 16);

    /// <summary>What the count for a mobile number is kept under: the keyed hash of the number, written as HC360 stores numbers.</summary>
    internal string SubjectHash(string subject)
    {
        var normalized = SharedKernel.Phone.Normalize(subject) ?? subject;
        return Convert.ToHexString(HMACSHA256.HashData(_subjectKey, Encoding.UTF8.GetBytes(normalized)), 0, 16);
    }
}

/// <summary>
/// Issues and checks the proof of work the portal's anonymous endpoints ask for (SRS SEC-06, ASVS V2.4.1). A refused
/// proof gets one generic answer whatever the reason; the reason is counted
/// (<c>hc360.anti_automation.rejected</c>) and, unless it is <see cref="ProofOfWorkOutcome.TooEasy"/>
/// (<see cref="IsFailure"/>), audited, at most once per address and reason in 5 minutes on any server
/// (<see cref="AutomationSuspicion.IsFirstRefusalAsync"/>) so a flood of bad proofs cannot flood the audit trail.
/// </summary>
public sealed class AntiAutomation(
    ProofOfWork proofOfWork,
    AutomationSuspicion suspicion,
    ISettingsService settings,
    IAuditLogger audit,
    TimeProvider time)
{
    public const string ProofHeader = "X-HC360-Proof";
    public const string MeterName = "HyFib.HealthCare360.AntiAutomation";

    /// <summary>Long enough to fill in a form after the browser solved the challenge in the background.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

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

        var bits = await suspicion.RequiredBitsAsync(config.Bits, config.MaxBits, address, subject, ct);
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

        var required = await suspicion.RequiredBitsAsync(config.Bits, config.MaxBits, address, subject, ct);
        var outcome = await proofOfWork.VerifyAsync(proof, action, required, ct);
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

        await suspicion.RecordFailureAsync(address, subject);
        if (!await suspicion.IsFirstRefusalAsync(address, reason, ct))
        {
            return outcome;
        }

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
    public Task RecordFailureAsync(string? address, string? subject) => suspicion.RecordFailureAsync(address, subject);

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

/// <summary>The proof of work's services, as the platform registers them.</summary>
internal static class AntiAutomationServices
{
    /// <summary>
    /// The portal's proof of work (SRS SEC-06): challenges signed with the anti-automation key, spent ones remembered, and
    /// the addresses and numbers that failed counted as hashes keyed with it; the key is read (or made) once, for both. What
    /// it remembers is the hospital's on a node of several (<see cref="DataDirectories.IsShared"/>), in the cluster store
    /// (SCAL-01): a challenge is spent once whichever node it is sent to, failures counted by one node raise the work every
    /// node asks for, and a refusal is audited once. A single Server Node keeps it in memory, as it keeps its rate limits.
    /// </summary>
    public static IServiceCollection AddAntiAutomation(this IServiceCollection services, DataDirectories directories)
    {
        services.AddSingleton<MemoryClusterStore>();
        services.AddSingleton(sp => new AntiAutomationKey(ProofOfWorkKey.LoadOrCreate(directories, sp.GetRequiredService<IDataProtectionProvider>())));
        services.AddSingleton(sp => new AutomationSuspicion(
            Store(sp),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AntiAutomationKey>().Value,
            sp.GetRequiredService<ILogger<AutomationSuspicion>>()));
        services.AddSingleton(sp => new ProofOfWork(sp.GetRequiredService<AntiAutomationKey>().Value, Store(sp), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<AntiAutomation>();
        return services;

        IClusterStore Store(IServiceProvider sp) =>
            directories.IsShared ? sp.GetRequiredService<IClusterStore>() : sp.GetRequiredService<MemoryClusterStore>();
    }
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
            var address = CountedAddress(http);
            var outcome = await check.CheckAsync(http.Request.Headers[AntiAutomation.ProofHeader].ToString(), action, who, address, http.RequestAborted);
            if (outcome != ProofOfWorkOutcome.Accepted)
            {
                // CheckAsync counted it against the address, and against the number when the request names it.
                if (who is null && subjectOnFailure is not null && AntiAutomation.IsFailure(outcome))
                {
                    await check.RecordFailureAsync(null, await subjectOnFailure(context, http.RequestAborted));
                }

                return Failed();
            }

            var result = await next(context);
            if (result is IStatusCodeHttpResult { StatusCode: >= 400 and < 500 })
            {
                await check.RecordFailureAsync(address, who ?? (subjectOnFailure is null ? null : await subjectOnFailure(context, http.RequestAborted)));
            }

            return result;
        });
        return builder;
    }

    /// <summary>A rate-limit refusal on an endpoint that asks for a proof raises the work asked of the address.</summary>
    internal static async ValueTask OnRateLimitedAsync(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<RequiresProofOfWorkMetadata>() is not null)
        {
            await http.RequestServices.GetRequiredService<AutomationSuspicion>().RecordFailureAsync(CountedAddress(http), null);
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

        return TypedResults.Ok(await antiAutomation.IssueAsync(request.Action, request.Subject, CountedAddress(http), ct));
    }

    /// <summary>What failures are counted against for the client, as the rate limits count it (an IPv6 /64 as one), if it has an address.</summary>
    private static string? CountedAddress(HttpContext http) =>
        http.Connection.RemoteIpAddress is null ? null : RateLimitPolicies.ClientKey(http);
}
