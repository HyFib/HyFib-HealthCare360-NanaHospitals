using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace HyFib.HealthCare360.WindowsInterop.Biometrics;

/// <summary>Why a fingerprint could not be captured, for the code the staff app is given.</summary>
public enum RdProblem
{
    /// <summary>No RD service answers on this computer.</summary>
    NoReader,

    /// <summary>The RD service runs, but its device is not connected or not ready.</summary>
    NotReady,

    /// <summary>Another application is using the device.</summary>
    Busy,

    /// <summary>The device waited for a finger and timed out.</summary>
    TimedOut,

    /// <summary>The device could not capture the finger.</summary>
    CaptureFailed,

    /// <summary>The RD service answered with something HC360 cannot use.</summary>
    BadAnswer,
}

/// <summary>A capture that failed, with words for staff (<see cref="Exception.Message"/>).</summary>
public sealed class RdServiceException(RdProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public RdProblem Problem { get; } = problem;
}

/// <summary>
/// Talks to the UIDAI RD services on this computer (<see cref="RdService"/>): finds them, asks one which device it serves,
/// and asks it for a fingerprint. Only <c>127.0.0.1</c> and the ports UIDAI set aside are ever called, on paths checked
/// by <see cref="RdService.IsLocalPath"/>, so neither a page nor an RD service can point HC360 anywhere else. The
/// capture it returns is encrypted for UIDAI; it is passed on as it came and never written to the log.
/// </summary>
public sealed class RdServiceClient(HttpMessageInvoker http)
{
    /// <summary>How long a port is given to answer discovery: a local service answers at once or not at all.</summary>
    public static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long the device is given to describe itself.</summary>
    public static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(10);

    /// <summary>What a capture may take beyond the device's own timeout: its prompt, and encrypting the capture.</summary>
    public static readonly TimeSpan CaptureMargin = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The handler to give this client in HC360 Desktop: no proxy (a system proxy must never see a capture), no
    /// redirects, no cookies, and a new connection for each call, since RD services are small single-threaded servers.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.Zero,
        MaxResponseHeadersLength = 16,
    };

    /// <summary>The RD services that answer on this computer, in port order.</summary>
    public async Task<IReadOnlyList<RdServiceEndpoint>> DiscoverAsync(CancellationToken ct)
    {
        var probes = Enumerable.Range(RdService.FirstPort, RdService.LastPort - RdService.FirstPort + 1)
            .Select(port => ProbeAsync(port, ct))
            .ToList();
        var answers = await Task.WhenAll(probes);
        return answers.OfType<RdServiceEndpoint>().OrderBy(e => e.Port).ToList();
    }

    /// <summary>The device an RD service serves (its DEVICEINFO interface).</summary>
    /// <exception cref="RdServiceException">The service could not be asked, or its answer could not be read.</exception>
    public async Task<RdDevice> DeviceAsync(RdServiceEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.InfoPath is null)
        {
            throw new RdServiceException(RdProblem.BadAnswer, "The fingerprint reader's RD service does not say where to ask about its device.");
        }

        var xml = await SendAsync(RdService.Info, endpoint.Port, endpoint.InfoPath, body: null, InfoTimeout, ct);
        try
        {
            return RdService.ParseDevice(xml);
        }
        catch (FormatException ex)
        {
            throw new RdServiceException(RdProblem.BadAnswer, ex.Message, ex);
        }
    }

    /// <summary>
    /// Asks an RD service for a capture. The answer is returned whatever its outcome; <see cref="CaptureFingerprintAsync"/>
    /// turns an unsuccessful one into an <see cref="RdServiceException"/>.
    /// </summary>
    /// <exception cref="RdServiceException">The service could not be asked, or its answer could not be read.</exception>
    public async Task<PidCapture> CaptureAsync(RdServiceEndpoint endpoint, FingerprintCaptureOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);
        if (endpoint.CapturePath is null)
        {
            throw new RdServiceException(RdProblem.BadAnswer, "The fingerprint reader's RD service does not say where to ask for a capture.");
        }

        var xml = await SendAsync(RdService.Capture, endpoint.Port, endpoint.CapturePath, options.ToXml(), options.Timeout + CaptureMargin, ct);
        try
        {
            return PidCapture.Parse(xml);
        }
        catch (FormatException ex)
        {
            throw new RdServiceException(RdProblem.BadAnswer, ex.Message, ex);
        }
    }

    /// <summary>
    /// Finds the fingerprint reader and captures: the first RD service, in port order, that is ready to capture.
    /// </summary>
    /// <exception cref="RdServiceException">No reader is ready, or the capture failed, with what to do about it.</exception>
    public async Task<PidCapture> CaptureFingerprintAsync(FingerprintCaptureOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var services = await DiscoverAsync(ct);
        var service = services.FirstOrDefault(s => s.CanCapture) ?? throw NotReady(services);
        var capture = await CaptureAsync(service, options, ct);
        if (capture.Succeeded)
        {
            return capture;
        }

        if (capture.ErrorCode == 0)
        {
            throw new RdServiceException(RdProblem.BadAnswer, "The fingerprint reader said it captured the finger but sent no fingerprint. Restart its RD service and try again.");
        }

        throw new RdServiceException(ProblemOf(capture.ErrorCode), RdService.Problem(capture.ErrorCode, capture.ErrorInfo));
    }

    /// <summary>Why no RD service can capture, from what discovery found.</summary>
    public static RdServiceException NotReady(IReadOnlyList<RdServiceEndpoint> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Count == 0)
        {
            return new RdServiceException(RdProblem.NoReader,
                "No fingerprint reader was found on this computer. Check that it is plugged in, and that its RD service is installed and running.");
        }

        if (services.Any(s => s.Status == RdServiceStatus.Used))
        {
            return new RdServiceException(RdProblem.Busy, "Another application is using the fingerprint reader. Close it and try again.");
        }

        var notReady = services.FirstOrDefault(s => s.Status == RdServiceStatus.NotReady);
        if (notReady is not null)
        {
            var info = string.IsNullOrWhiteSpace(notReady.Info) ? string.Empty : $" ({Shorten(notReady.Info)})";
            return new RdServiceException(RdProblem.NotReady,
                $"The fingerprint reader is not ready{info}. Plug it in again, or restart its RD service.");
        }

        return new RdServiceException(RdProblem.BadAnswer,
            "The fingerprint reader's RD service did not say it can capture. Restart it, or reinstall it from the device maker.");
    }

    /// <summary>The kind of problem an RD error code stands for (UIDAI's RD error codes).</summary>
    public static RdProblem ProblemOf(int errorCode) => errorCode switch
    {
        700 => RdProblem.TimedOut,
        710 => RdProblem.Busy,
        720 => RdProblem.NotReady,
        _ => RdProblem.CaptureFailed,
    };

    private async Task<RdServiceEndpoint?> ProbeAsync(int port, CancellationToken ct)
    {
        try
        {
            var xml = await SendAsync(RdService.Discover, port, "/", body: null, DiscoveryTimeout, ct);
            return RdService.ParseEndpoint(port, xml);
        }
        catch (RdServiceException)
        {
            // Nothing listens on the port, or something that is not an RD service does.
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<string> SendAsync(HttpMethod method, int port, string path, string? body, TimeSpan timeout, CancellationToken ct)
    {
        if (port is < RdService.FirstPort or > RdService.LastPort || !RdService.IsLocalPath(path))
        {
            throw new RdServiceException(RdProblem.BadAnswer, "The fingerprint reader's RD service named an address HC360 does not call.");
        }

        using var request = new HttpRequestMessage(method, new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{path}")));
        request.Headers.ConnectionClose = true;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, timer.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new RdServiceException(RdProblem.BadAnswer,
                    $"The fingerprint reader's RD service answered HTTP {(int)response.StatusCode}. Restart it and try again.");
            }

            return await ReadAsync(response, timer.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RdServiceException(RdProblem.TimedOut, "The fingerprint reader did not answer in time. Restart its RD service and try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RdServiceException(RdProblem.NoReader, "The fingerprint reader's RD service could not be reached.", ex);
        }
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > RdService.MaxDocumentChars)
        {
            throw new RdServiceException(RdProblem.BadAnswer, "The fingerprint reader's answer is too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > RdService.MaxDocumentChars)
            {
                throw new RdServiceException(RdProblem.BadAnswer, "The fingerprint reader's answer is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Shorten(string text) => text.Length <= 80 ? text.Trim() : text[..80].Trim() + "…";
}
