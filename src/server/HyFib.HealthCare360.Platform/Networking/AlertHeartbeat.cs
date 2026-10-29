using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>What the outside heartbeat last did, on the server that sends it (<see cref="AlertHeartbeat"/>).</summary>
/// <param name="Host">
/// The host it fetches, never the rest of the address, whose path or query holds the monitoring service's token; null while
/// no address is set.
/// </param>
/// <param name="Failures">Fetches that failed in a row.</param>
/// <param name="FailingSince">When the first of them failed.</param>
/// <param name="LastError">Why the latest attempt sent nothing, or failed.</param>
public sealed record HeartbeatState(
    string? Host, int Minutes, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, int Failures, DateTimeOffset? FailingSince, string? LastError)
{
    public static readonly HeartbeatState Off = new(null, AlertHeartbeat.DefaultMinutes, null, null, 0, null, null);
}

/// <summary>
/// A dead-man's switch (audit P2). Everything that tells IT about a problem runs in the server itself
/// (<see cref="SystemHealthAlerts"/>), so a power cut, a stopped HC360Server service, a server that cannot reach its
/// database, or a site cut off from the internet tells nobody. So every few minutes (<c>alerts.heartbeatMinutes</c>, 5 if
/// not set) the server that runs the jobs fetches the address the hospital set (<see cref="UrlSetting"/>) from a
/// monitoring service outside the hospital, which raises its own alarm when the fetches stop. It fetches only while the
/// database answers, so a server that cannot reach it counts as down. The address is https, or http to an address on the
/// hospital's own network; the fetch carries nothing but the address, and follows no redirect (<see cref="Http.OutboundHttp"/>).
/// </summary>
public sealed class AlertHeartbeat(
    IServiceScopeFactory scopes, IHttpClientFactory http, IOptions<PlatformOptions> options, TimeProvider time, ILogger<AlertHeartbeat> logger)
    : BackgroundService
{
    /// <summary>
    /// The address to fetch. Its path or query holds the monitoring service's token, and whoever has it could fetch it and
    /// hide an outage: a secret setting (<see cref="SecretSettingKeys"/>), kept encrypted, never shown again, and left out
    /// of the audit trail, however it is saved.
    /// </summary>
    public const string UrlSetting = "alerts.heartbeatUrl.secret";
    public const string MinutesSetting = "alerts.heartbeatMinutes";
    public const string HttpClientName = "alerts.heartbeat";
    public const string FailingCode = "alerts.heartbeat_failing";
    public const string OffCode = "alerts.heartbeat_off";
    public const int DefaultMinutes = 5;

    /// <summary>Failed fetches in a row before System health says so: a quarter of an hour at the default period.</summary>
    public const int FailuresToWarn = 3;

    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _beating = new(1, 1);
    private HeartbeatState _state = HeartbeatState.Off;

    // The whole address, kept out of the state: a new one starts the count again.
    private string? _address;

    public HeartbeatState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            try
            {
                await BeatAsync(force: false, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The outside heartbeat could not be sent");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Fetches the heartbeat address when it is due, or now with <paramref name="force"/>, and returns what happened.</summary>
    public async Task<HeartbeatState> BeatAsync(bool force, CancellationToken ct)
    {
        await _beating.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var (address, minutes) = await ReadAsync(scope.ServiceProvider.GetRequiredService<ISettingsService>(), ct);
            if (address is null)
            {
                return Set(HeartbeatState.Off, null);
            }

            var state = State;
            if (!string.Equals(_address, address.AbsoluteUri, StringComparison.Ordinal))
            {
                state = HeartbeatState.Off with { Host = address.Host };
            }

            var now = time.GetUtcNow();
            if (!force && state.LastAttemptAt is { } last && now - last < TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(5))
            {
                return state;
            }

            // Withheld while the database does not answer: the monitoring service raises its alarm, as it should.
            if (!await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.CanConnectAsync(ct))
            {
                return Set(state with { Minutes = minutes, LastAttemptAt = now, LastError = "the database does not answer, so no heartbeat was sent." },
                    address.AbsoluteUri);
            }

            var error = await FetchAsync(address, ct);
            if (error is null)
            {
                return Set(state with { Minutes = minutes, LastAttemptAt = now, LastSuccessAt = now, Failures = 0, FailingSince = null, LastError = null },
                    address.AbsoluteUri);
            }

            logger.LogWarning("The outside heartbeat to {Host} failed: {Error}", address.Host, error);
            return Set(state with
            {
                Minutes = minutes,
                LastAttemptAt = now,
                Failures = state.Failures + 1,
                FailingSince = state.FailingSince ?? now,
                LastError = error,
            }, address.AbsoluteUri);
        }
        finally
        {
            _beating.Release();
        }
    }

    /// <summary>The job server's warning once <see cref="FailuresToWarn"/> fetches in a row have failed.</summary>
    public static HealthWarning? Warning(HeartbeatState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Host is null || state.Failures < FailuresToWarn
            ? null
            : new HealthWarning(FailingCode, "warning",
                string.Create(CultureInfo.InvariantCulture,
                    $"The outside heartbeat to {state.Host} has failed {state.Failures} times in a row, since {state.FailingSince:dd MMM yyyy HH:mm} UTC: {state.LastError} ")
                + $"The monitoring service raises its alarm when the heartbeats stop. Check this server's internet access, and the address under Administration › Settings ({UrlSetting}).");
    }

    /// <summary>On a live server: the warning that no heartbeat goes out, because no address is set or the one set is refused.</summary>
    public static async Task<HealthWarning?> OffWarningAsync(ISettingsService settings, PlatformOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowsSimulators)
        {
            return null;
        }

        var value = await TextAsync(settings, UrlSetting, ct);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HealthWarning(OffCode, "warning",
                $"Nothing outside the hospital notices if this server stops: its own alerts stop with it. Set {UrlSetting} under Administration › Settings "
                + "to the address a heartbeat monitor gives, as the deployment guide says (Alerts that reach someone).");
        }

        return TryParse(value, out _, out var problem)
            ? null
            : new HealthWarning(OffCode, "warning",
                $"The heartbeat address ({UrlSetting}) {problem}, so no heartbeat goes out and nothing outside the hospital notices if this server stops.");
    }

    /// <summary>
    /// The heartbeat address in <paramref name="value"/>: https, or http to this machine or a private address of the
    /// hospital's network (a monitor on another machine there), with no user name or password in it.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out Uri? address, out string? problem)
    {
        address = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            problem = "is not a web address";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            problem = "has a user name or password in it";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !OnHospitalNetwork(uri))
        {
            problem = "is http to an address outside the hospital's network: use its https address";
            return false;
        }

        address = uri;
        return true;
    }

    internal static async Task<(Uri? Address, int Minutes)> ReadAsync(ISettingsService settings, CancellationToken ct)
    {
        var minutes = await settings.GetAsync<JsonElement?>(MinutesSetting, cancellationToken: ct) switch
        {
            { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var value) => value,
            { ValueKind: JsonValueKind.String } text when int.TryParse(text.GetString(), CultureInfo.InvariantCulture, out var value) => value,
            _ => DefaultMinutes,
        };
        return (TryParse(await TextAsync(settings, UrlSetting, ct), out var address, out _) ? address : null, Math.Clamp(minutes, 1, 60));
    }

    private static async Task<string?> TextAsync(ISettingsService settings, string key, CancellationToken ct) =>
        await settings.GetAsync<JsonElement?>(key, cancellationToken: ct) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    private static bool OnHospitalNetwork(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.DnsSafeHost, out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal;
        }

        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }

    private async Task<string?> FetchAsync(Uri address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var client = http.CreateClient(HttpClientName);
            using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"the monitoring service answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return string.Create(CultureInfo.InvariantCulture, $"no answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            // Said without the address's path or query, which hold the token.
            return $"it could not be reached ({Hide(ex.Message, address)}).";
        }
    }

    private static string Hide(string message, Uri address) =>
        address.PathAndQuery.Length > 1
            ? message.Replace(address.AbsoluteUri, address.Host, StringComparison.Ordinal).Replace(address.PathAndQuery, string.Empty, StringComparison.Ordinal)
            : message;

    private HeartbeatState Set(HeartbeatState state, string? address)
    {
        lock (_gate)
        {
            _state = state;
            _address = address;
            return state;
        }
    }

    public override void Dispose()
    {
        _beating.Dispose();
        base.Dispose();
    }
}

/// <summary>The heartbeat's warning in System health, from the server that sends it: the one that runs the jobs.</summary>
internal sealed class AlertHeartbeatWarnings(AlertHeartbeat heartbeat) : IHealthWarningSource
{
    public Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HealthWarning>>(AlertHeartbeat.Warning(heartbeat.State) is { } warning ? [warning] : []);
}
