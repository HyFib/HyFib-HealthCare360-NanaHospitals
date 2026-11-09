using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Platform.Logging;

/// <summary>What the syslog collector has had from this server: when a message last reached it, when one last did not, and why.</summary>
/// <param name="Dropped">Log events not sent because <see cref="SyslogOptions.QueueLimit"/> were already waiting.</param>
public sealed record SyslogStatus(
    bool Enabled, string? Collector, DateTimeOffset? LastDeliveredAt, DateTimeOffset? LastFailedAt, string? LastError, long Dropped, int Waiting)
{
    /// <summary>The last attempt failed: nothing has reached the collector since.</summary>
    public bool Failing => LastFailedAt is { } failed && (LastDeliveredAt is not { } delivered || failed >= delivered);
}

/// <summary>
/// Sends messages to the syslog collector (<see cref="SyslogOptions"/>) over one TLS connection (RFC 5425), in the order
/// they come, each framed with its length. Log events wait in a queue of <see cref="SyslogOptions.QueueLimit"/> while the
/// collector cannot be reached, and are tried again, with a pause that doubles up to a minute; beyond the limit the newest
/// are dropped and counted (the log file still has them). <see cref="SendAsync"/> is for a message whose delivery the
/// caller must know about, such as an audit anchor: it fails at once when the collector cannot be reached, and is not
/// tried again here.
/// <para>
/// It logs nothing itself: its log events would come back to it. What it knows is in <see cref="Status"/>, which System
/// health shows (<see cref="SyslogHealthWarnings"/>). TCP does not say whether the collector kept a message: one written
/// just as the collector went away counts as delivered (RFC 5425 has no acknowledgement).
/// </para>
/// </summary>
public sealed class SyslogClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(1);

    private readonly SyslogOptions _options;
    private readonly TimeProvider _time;
    private readonly Channel<Pending>? _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _status = new();
    private readonly string _hostName = Environment.MachineName;
    private readonly string _processId = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private Task? _loop;
    private DateTimeOffset? _lastDeliveredAt;
    private DateTimeOffset? _lastFailedAt;
    private string? _lastError;
    private long _dropped;
    private int _waiting;

    public SyslogClient(SyslogOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = time;
        if (IsEnabled)
        {
            _queue = Channel.CreateBounded<Pending>(
                new BoundedChannelOptions(Math.Max(options.QueueLimit, 1)) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
                Dropped);
        }
    }

    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Host);

    /// <summary>host:port of the collector, or null when it is off.</summary>
    public string? Collector => IsEnabled ? $"{_options.Host}:{_options.Port}" : null;

    public SyslogStatus Status
    {
        get
        {
            lock (_status)
            {
                return new SyslogStatus(IsEnabled, Collector, _lastDeliveredAt, _lastFailedAt, _lastError, _dropped, Volatile.Read(ref _waiting));
            }
        }
    }

    /// <summary>Queues a message, and returns at once. Nothing happens when the collector is off.</summary>
    public void Enqueue(SyslogEntry entry)
    {
        if (_queue is null)
        {
            return;
        }

        EnsureStarted();
        Interlocked.Increment(ref _waiting);
        if (!_queue.Writer.TryWrite(new Pending(Frame(entry), null)))
        {
            // Stopping: the log file has it.
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>
    /// Sends a message and waits until it is written to the collector's connection. Throws when the collector is off, cannot
    /// be reached, or the message was not written before <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task SendAsync(SyslogEntry entry, CancellationToken cancellationToken)
    {
        if (_queue is null)
        {
            throw new InvalidOperationException("The syslog collector is off (HealthCare360:Syslog:Enabled).");
        }

        EnsureStarted();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _waiting);
        if (!_queue.Writer.TryWrite(new Pending(Frame(entry), delivered)))
        {
            Interlocked.Decrement(ref _waiting);
            throw new InvalidOperationException("The syslog client is stopping.");
        }

        await delivered.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_queue is null)
        {
            _stop.Dispose();
            return;
        }

        _queue.Writer.TryComplete();
        if (_loop is { } loop)
        {
            // What is waiting gets a moment to go, the shutdown's own log events among them.
            await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(3), _time));
        }

        await _stop.CancelAsync();
        if (_loop is { } stopped)
        {
            await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(1), _time));
        }

        _stop.Dispose();
    }

    private byte[] Frame(SyslogEntry entry) =>
        SyslogFormat.Frame(SyslogFormat.Message(entry, _options.Facility, _hostName, _options.AppName, _processId));

    private void Dropped(Pending pending)
    {
        Interlocked.Decrement(ref _waiting);
        pending.Delivered?.TrySetException(new InvalidOperationException($"{_options.QueueLimit} messages already wait for the syslog collector."));
        lock (_status)
        {
            _dropped++;
        }
    }

    private void EnsureStarted()
    {
        if (_loop is null)
        {
            lock (_status)
            {
                _loop ??= Task.Run(() => RunAsync(_stop.Token));
            }
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        Connection? connection = null;
        var pause = TimeSpan.FromSeconds(1);
        try
        {
            await foreach (var pending in _queue!.Reader.ReadAllAsync(stop))
            {
                while (true)
                {
                    try
                    {
                        connection ??= await ConnectAsync(stop);
                        await connection.Stream.WriteAsync(pending.Frame, stop);
                        await connection.Stream.FlushAsync(stop);
                        Interlocked.Decrement(ref _waiting);
                        lock (_status)
                        {
                            _lastDeliveredAt = _time.GetUtcNow();
                        }

                        pending.Delivered?.TrySetResult();
                        pause = TimeSpan.FromSeconds(1);
                        break;
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested)
                    {
                        connection?.Dispose();
                        connection = null;
                        lock (_status)
                        {
                            _lastFailedAt = _time.GetUtcNow();
                            _lastError = SensitiveText.Redact(Reason(ex));
                        }

                        if (pending.Delivered is { } delivered)
                        {
                            // The caller is told now, and decides when to try again.
                            Interlocked.Decrement(ref _waiting);
                            delivered.TrySetException(ex);
                            break;
                        }

                        await Task.Delay(pause, _time, stop);
                        pause = pause * 2 > MaxPause ? MaxPause : pause * 2;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private async Task<Connection> ConnectAsync(CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(ConnectTimeout);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(_options.Host!, _options.Port, timeout.Token);
            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            var authentication = new SslClientAuthenticationOptions
            {
                TargetHost = _options.Host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = Validate,
            };
            if (!string.IsNullOrWhiteSpace(_options.ClientCertificatePath))
            {
                authentication.ClientCertificates =
                [
                    X509CertificateLoader.LoadPkcs12FromFile(Environment.ExpandEnvironmentVariables(_options.ClientCertificatePath), _options.ClientCertificatePassword),
                ];
            }

            await tls.AuthenticateAsClientAsync(authentication, timeout.Token);
            return new Connection(tcp, tls);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The collector's certificate must name it and chain to a trusted root: the machine's, or the CA in
    /// <see cref="SyslogOptions.CaCertificatePath"/> alone.
    /// </summary>
    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (string.IsNullOrWhiteSpace(_options.CaCertificatePath))
        {
            return errors == SslPolicyErrors.None;
        }

        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        using var ca = X509CertificateLoader.LoadCertificateFromFile(Environment.ExpandEnvironmentVariables(_options.CaCertificatePath));
        using var server = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var pinned = new X509Chain();
        pinned.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        pinned.ChainPolicy.CustomTrustStore.Add(ca);
        pinned.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        // A private CA on the hospital's network publishes no revocation lists.
        pinned.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements)
            {
                pinned.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return pinned.Build(server);
    }

    private static string Reason(Exception ex) => ex switch
    {
        AuthenticationException => $"The TLS handshake failed: {ex.Message} {ex.InnerException?.Message}".Trim(),
        SocketException socket => $"{socket.SocketErrorCode}: {socket.Message}",
        OperationCanceledException => "No answer within 15 seconds.",
        _ => ex.Message,
    };

    private sealed record Pending(byte[] Frame, TaskCompletionSource? Delivered);

    private sealed class Connection(TcpClient tcp, SslStream stream) : IDisposable
    {
        public SslStream Stream { get; } = stream;

        public void Dispose()
        {
            Stream.Dispose();
            tcp.Dispose();
        }
    }
}

/// <summary>System health's warnings about the syslog collector, from each server about its own log events.</summary>
internal sealed class SyslogHealthWarnings(SyslogClient syslog) : IHealthWarningSource
{
    public Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Warnings(syslog.Status));

    internal static IReadOnlyList<HealthWarning> Warnings(SyslogStatus status)
    {
        if (!status.Enabled)
        {
            return [];
        }

        var warnings = new List<HealthWarning>();
        if (status.Failing)
        {
            warnings.Add(new("logs.syslog_unreachable", "warning",
                $"Log events cannot reach the syslog collector {status.Collector} since {status.LastFailedAt:yyyy-MM-dd HH:mm} UTC: {status.LastError} " +
                $"They are kept in this server's log file, and {status.Waiting:N0} wait to be sent once it answers."));
        }

        if (status.Dropped > 0)
        {
            warnings.Add(new("logs.syslog_dropped", "warning",
                $"{status.Dropped:N0} log events were not sent to the syslog collector {status.Collector}: too many were waiting. " +
                "This server's log file still has them; copy it to the collector, or raise HealthCare360:Syslog:QueueLimit."));
        }

        return warnings;
    }
}
