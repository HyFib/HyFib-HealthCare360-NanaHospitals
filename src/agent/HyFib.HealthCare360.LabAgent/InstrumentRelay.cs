using System.Net.WebSockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>
/// One serial analyser: opens its COM port and keeps it open (closing it can reset some analysers), and relays it to the
/// server for as long as the agent runs. A lost connection is opened again after a wait that grows to a minute, and the
/// wait starts again from a second once a relay has run for a minute. A port that cannot be opened is tried again the
/// same way, and the reason goes to the server with the next heartbeat.
/// </summary>
internal sealed class InstrumentRelay : IAsyncDisposable
{
    private static readonly TimeSpan HealthyRelay = TimeSpan.FromMinutes(1);

    private readonly ISerialLineFactory _lines;
    private readonly IRelayConnector _connector;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Backoff _portRetry;
    private readonly Backoff _relayRetry;
    private readonly CancellationTokenSource _stopping = new();
    private Task _running = Task.CompletedTask;
    private volatile PortReport _report;

    public InstrumentRelay(Assignment assignment, ISerialLineFactory lines, IRelayConnector connector, TimeProvider time, ILogger logger,
        Backoff? portRetry = null, Backoff? relayRetry = null)
    {
        Assignment = assignment;
        _lines = lines;
        _connector = connector;
        _time = time;
        _logger = logger;
        _portRetry = portRetry ?? new Backoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
        _relayRetry = relayRetry ?? new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        _report = new PortReport(assignment.Code, false, "Starting.");
    }

    public Assignment Assignment { get; }

    /// <summary>Whether the COM port is open, or why not, for the next heartbeat.</summary>
    public PortReport Report => _report;

    public void Start() => _running = RunAsync(_stopping.Token);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _running;
        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var port = Assignment.Port.PortName;
        while (!cancellationToken.IsCancellationRequested)
        {
            ISerialLine line;
            try
            {
                line = _lines.Open(Assignment.Port);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
            {
                // Windows' reason ("Access to the port 'COM3' is denied." when another program holds it) carries no analyser data.
                _report = new PortReport(Assignment.Code, false, Truncate(ex.Message));
                _logger.LogWarning("{Port} for analyser {Instrument} could not be opened: {Reason}", port, Assignment.Code, ex.Message);
                await WaitAsync(_portRetry.Next(), cancellationToken);
                continue;
            }

            _portRetry.Reset();
            _report = new PortReport(Assignment.Code, true, null);
            _logger.LogInformation("Opened {Port} for analyser {Instrument}", port, Assignment.Code);
            using (line)
            {
                await RelayAsync(line, cancellationToken);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _report = new PortReport(Assignment.Code, false, $"{port} stopped working; opening it again.");
                _logger.LogWarning("{Port} for analyser {Instrument} stopped working; opening it again", port, Assignment.Code);
                await WaitAsync(_portRetry.Next(), cancellationToken);
            }
        }
    }

    /// <summary>Relays the open line, reconnecting to the server as needed, until the port fails or the agent stops.</summary>
    private async Task RelayAsync(ISerialLine line, CancellationToken cancellationToken)
    {
        await using var pump = new LinePump(line.Stream, _logger);
        while (!cancellationToken.IsCancellationRequested && !pump.Failed)
        {
            WebSocket socket;
            try
            {
                socket = await _connector.ConnectAsync(Assignment.Code, cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or AuthenticationException
                                           || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning("Could not connect the relay of analyser {Instrument}: {Reason}", Assignment.Code, ex.Message);
                await WaitAsync(_relayRetry.Next(), cancellationToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using (socket)
            {
                // Bytes from before this relay belong to an exchange the server never saw; the analyser repeats it.
                line.DiscardInput();
                var started = _time.GetTimestamp();
                _logger.LogInformation("Relaying analyser {Instrument} to the server", Assignment.Code);
                var reason = await pump.RelayAsync(socket, cancellationToken);
                var (toServer, toAnalyser, dropped) = pump.Traffic;
                _logger.LogInformation(
                    "Relay of analyser {Instrument} ended: {Reason} ({ToServer} bytes to the server, {ToAnalyser} to the analyser, {Dropped} dropped so far)",
                    Assignment.Code, reason, toServer, toAnalyser, dropped);
                if (_time.GetElapsedTime(started) >= HealthyRelay)
                {
                    _relayRetry.Reset();
                }
            }

            if (!cancellationToken.IsCancellationRequested && !pump.Failed)
            {
                await WaitAsync(_relayRetry.Next(), cancellationToken);
            }
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];
}
