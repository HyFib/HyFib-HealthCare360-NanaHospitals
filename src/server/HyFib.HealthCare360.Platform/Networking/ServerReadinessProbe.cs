using System.Globalization;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// Asks this server's own web host whether it is ready, over HTTP, as a staff browser would reach it. The database check
/// alone cannot tell a wedged web host (thread pool starved, Kestrel stuck) from a healthy one, and the outside heartbeat
/// (<see cref="AlertHeartbeat"/>) must stop when staff cannot work.
/// </summary>
public interface IServerReadinessProbe
{
    /// <summary>Null when <c>/health/ready</c> answered 200 in time; otherwise a sentence saying why not.</summary>
    Task<string?> CheckAsync(CancellationToken ct);
}

/// <summary>
/// The loopback request to <c>/health/ready</c> on the staff listener, with a short timeout. It trusts one certificate only,
/// the server's own LAN certificate (<see cref="LanCertificateManager.ServerCertificate"/>, read afresh each time because it
/// is issued again while the server runs), so the check cannot be answered by anything else listening on that port, and the
/// machine's certificate store plays no part.
/// </summary>
public sealed class LoopbackReadinessProbe : IServerReadinessProbe, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _client;
    private readonly Uri _address;

    private readonly TimeSpan _timeout;

    /// <param name="httpsPort">The staff listener's port (<see cref="Hosting.PlatformOptions.HttpsPort"/>).</param>
    /// <param name="ownCertificate">The server's own certificate as it is now; the one certificate the request trusts.</param>
    /// <param name="timeout">How long it waits for the answer; <see cref="Timeout"/> if not given.</param>
    public LoopbackReadinessProbe(int httpsPort, Func<X509Certificate2?> ownCertificate, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? Timeout;
        _address = new Uri(string.Create(CultureInfo.InvariantCulture, $"https://127.0.0.1:{httpsPort}/health/ready"));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = _timeout,
            SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => IsOwn(presented, ownCertificate()) },
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = _timeout };
    }

    /// <summary>True only when <paramref name="presented"/> is exactly the server's own certificate; the host name is not compared, as the request is to 127.0.0.1.</summary>
    internal static bool IsOwn(X509Certificate? presented, X509Certificate2? own) =>
        presented is not null && own is not null && presented.GetRawCertData().AsSpan().SequenceEqual(own.GetRawCertData());

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _client.GetAsync(_address, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"the web host answered its own readiness check {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return string.Create(CultureInfo.InvariantCulture, $"the web host did not answer its own readiness check within {_timeout.TotalSeconds:0.#} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return $"the web host could not be reached on its own address ({ex.Message}).";
        }
    }

    public void Dispose() => _client.Dispose();
}
