using System.ComponentModel;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Documents;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Integrations.Pdf;

/// <summary>
/// Makes PDFs with headless Microsoft Edge (ADR-009). One browser serves every PDF: it starts with the first one, is
/// driven over a private pipe (no network port), and closes after <see cref="PdfOptions.IdleMinutes"/> without work or
/// after <see cref="PdfOptions.RecycleAfter"/> PDFs. Each PDF is printed in a tab of its own browser context with
/// scripts off and the network blocked, so a document can neither run code nor fetch anything.
/// </summary>
internal sealed class EdgePdfRenderer : IPdfRenderer, IAsyncDisposable
{
    private const string NoBrowser =
        "Microsoft Edge was not found on the server, so PDFs can't be made. Install Edge, or set HealthCare360:Pdf:BrowserPath.";

    private readonly PdfOptions _options;
    private readonly string? _browserPath;
    private readonly string _profileRoot;
    private readonly ILogger<EdgePdfRenderer> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ITimer _idleTimer;
    private HeadlessBrowser? _browser;
    private string? _problem;
    private bool _disposed;

    public EdgePdfRenderer(IOptions<PlatformOptions> options, DataDirectories directories, TimeProvider time, ILogger<EdgePdfRenderer> logger)
    {
        _options = options.Value.Pdf;
        _logger = logger;
        _browserPath = BrowserLocator.Find(_options.BrowserPath);
        _profileRoot = Path.Combine(directories.Temp, "pdf");
        _slots = new SemaphoreSlim(Math.Clamp(_options.MaxConcurrent, 1, 8));
        _idleTimer = time.CreateTimer(_ => RetireWhenIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        HeadlessBrowser.DeleteLeftoverProfiles(_profileRoot, logger);
    }

    public PdfRendererStatus Status => _browserPath is null
        ? new PdfRendererStatus(false, null, NoBrowser)
        : new PdfRendererStatus(Volatile.Read(ref _problem) is null, _browserPath, Volatile.Read(ref _problem));

    public async Task<byte[]> RenderAsync(PdfSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_browserPath is null)
        {
            throw new PdfRenderException(NoBrowser);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)));
        HeadlessBrowser? browser = null;
        try
        {
            await _slots.WaitAsync(timeout.Token);
            try
            {
                browser = await LeaseBrowserAsync(timeout.Token);
                var pdf = await browser.PrintAsync(source, timeout.Token);
                Volatile.Write(ref _problem, null);
                return pdf;
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The browser may be stuck; the next PDF gets a fresh one.
            browser?.Retire();
            throw Failed("Making the PDF took too long.", null);
        }
        catch (DevToolsException ex)
        {
            browser?.Retire();
            throw Failed("The PDF could not be made.", ex);
        }
        finally
        {
            if (browser is not null)
            {
                await ReleaseAsync(browser);
            }

            if (!_disposed)
            {
                _idleTimer.Change(TimeSpan.FromMinutes(Math.Max(1, _options.IdleMinutes)), Timeout.InfiniteTimeSpan);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _idleTimer.DisposeAsync();
        await _startLock.WaitAsync();
        try
        {
            if (Interlocked.Exchange(ref _browser, null) is { } browser)
            {
                browser.Retire();
                await browser.DisposeAsync();
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<HeadlessBrowser> LeaseBrowserAsync(CancellationToken cancellationToken)
    {
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_browser is { } current && current.Renders < Math.Max(1, _options.RecycleAfter) && current.TryLease())
            {
                return current;
            }

            if (Interlocked.Exchange(ref _browser, null) is { } old && old.Retire())
            {
                _ = DisposeQuietlyAsync(old);
            }

            var started = await HeadlessBrowser.StartAsync(_browserPath!, _profileRoot, _logger, cancellationToken);
            _logger.LogInformation("Started {Browser} to make PDFs", started.Product);
            started.TryLease();
            _browser = started;
            return started;
        }
        catch (Exception ex) when (ex is DevToolsException or Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw Failed("The PDF browser (Microsoft Edge) could not be started.", ex);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ReleaseAsync(HeadlessBrowser browser)
    {
        if (browser.Release())
        {
            await DisposeQuietlyAsync(browser);
        }
    }

    // A timer callback: never wait for a render that is starting, just try again at the next idle period.
    private void RetireWhenIdle()
    {
        if (!_startLock.Wait(0))
        {
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref _browser, null) is { } idle && idle.Retire())
            {
                _logger.LogDebug("Closing the PDF browser after {Minutes} idle minutes", _options.IdleMinutes);
                _ = DisposeQuietlyAsync(idle);
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task DisposeQuietlyAsync(HeadlessBrowser browser)
    {
        try
        {
            await browser.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The PDF browser did not close cleanly");
        }
    }

    private PdfRenderException Failed(string message, Exception? cause)
    {
        Volatile.Write(ref _problem, message);
        _logger.LogWarning(cause, "PDF failed: {Reason}", message);
        return new PdfRenderException(message, cause);
    }

    /// <summary>One tab: a browser context of its own, scripts off, network blocked. Reused for up to 50 PDFs.</summary>
    internal sealed class BrowserTab
    {
        private const int MaxUses = 50;
        private const int ReadChunkBytes = 1024 * 1024;
        private const int MaxPdfBytes = 100 * 1024 * 1024;

        // Only real network schemes: data: images (logos, QR codes) must still load.
        private static readonly string[] BlockedUrls = ["http://*", "https://*", "ws://*", "wss://*", "ftp://*", "file://*"];

        private readonly DevToolsPipe _pipe;
        private readonly string _contextId;
        private readonly string _targetId;
        private readonly string _sessionId;
        private readonly string _frameId;

        private BrowserTab(DevToolsPipe pipe, string contextId, string targetId, string sessionId, string frameId)
        {
            _pipe = pipe;
            _contextId = contextId;
            _targetId = targetId;
            _sessionId = sessionId;
            _frameId = frameId;
        }

        public int Uses { get; private set; }

        public bool Reusable => Uses < MaxUses;

        public static async Task<BrowserTab> OpenAsync(DevToolsPipe pipe, CancellationToken cancellationToken)
        {
            var context = await pipe.SendAsync("Target.createBrowserContext", new JsonObject { ["disposeOnDetach"] = true }, null, cancellationToken);
            var contextId = context.GetProperty("browserContextId").GetString()!;
            var target = await pipe.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank", ["browserContextId"] = contextId }, null, cancellationToken);
            var targetId = target.GetProperty("targetId").GetString()!;
            var attached = await pipe.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, null, cancellationToken);
            var sessionId = attached.GetProperty("sessionId").GetString()!;

            await pipe.SendAsync("Emulation.setScriptExecutionDisabled", new JsonObject { ["value"] = true }, sessionId, cancellationToken);
            await pipe.SendAsync("Network.enable", null, sessionId, cancellationToken);
            await pipe.SendAsync("Network.setBlockedURLs", new JsonObject { ["urls"] = new JsonArray([.. BlockedUrls.Select(u => (JsonNode)u)]) }, sessionId, cancellationToken);
            await pipe.SendAsync("Page.enable", null, sessionId, cancellationToken);
            var tree = await pipe.SendAsync("Page.getFrameTree", null, sessionId, cancellationToken);
            var frameId = tree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
            return new BrowserTab(pipe, contextId, targetId, sessionId, frameId);
        }

        public async Task<byte[]> PrintAsync(PdfSource source, CancellationToken cancellationToken)
        {
            Uses++;

            // Wait for the load event, so that images (data: URIs decode asynchronously) are in place before printing.
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (_pipe.Listen(_sessionId, (method, _) =>
            {
                if (method == "Page.loadEventFired")
                {
                    loaded.TrySetResult();
                }
                else if (method == "Inspector.targetCrashed")
                {
                    loaded.TrySetException(new DevToolsException("The PDF browser tab crashed."));
                }
            }))
            {
                await _pipe.SendAsync("Page.setDocumentContent", new JsonObject { ["frameId"] = _frameId, ["html"] = source.Html }, _sessionId, cancellationToken);
                await loaded.Task.WaitAsync(cancellationToken);
            }

            // And for the document's own fonts (the Devanagari face, a data: URI that decodes asynchronously): the load
            // event can come first, and a page printed then has its Hindi in a fallback font, a different PDF from the
            // next print of the same page. DevTools evaluates this even though the page's own scripts are disabled.
            var fonts = await _pipe.SendAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = "document.fonts.ready.then(() => document.fonts.status)",
                ["awaitPromise"] = true,
                ["returnByValue"] = true,
            }, _sessionId, cancellationToken);
            if (fonts.TryGetProperty("exceptionDetails", out _)
                || !fonts.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var status) || status.GetString() != "loaded")
            {
                throw new DevToolsException($"The PDF browser could not wait for the document's fonts: {fonts.GetRawText()}");
            }

            // The document's own @page rules give the size, margins and running footer (CSS margin boxes), as when it
            // is printed from the browser. The margins here apply only to a page that sets none.
            var print = new JsonObject
            {
                ["printBackground"] = true,
                ["preferCSSPageSize"] = true,
                ["generateTaggedPDF"] = true,
                ["transferMode"] = "ReturnAsStream",
                ["displayHeaderFooter"] = false,
                ["marginTop"] = 0.4,
                ["marginBottom"] = 0.4,
                ["marginLeft"] = 0.4,
                ["marginRight"] = 0.4,
            };
            var printed = await _pipe.SendAsync("Page.printToPDF", print, _sessionId, cancellationToken);
            return await ReadStreamAsync(printed.GetProperty("stream").GetString()!, cancellationToken);
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            await _pipe.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = _targetId }, null, cancellationToken);
            await _pipe.SendAsync("Target.disposeBrowserContext", new JsonObject { ["browserContextId"] = _contextId }, null, cancellationToken);
        }

        private async Task<byte[]> ReadStreamAsync(string handle, CancellationToken cancellationToken)
        {
            using var pdf = new MemoryStream();
            while (true)
            {
                var chunk = await _pipe.SendAsync("IO.read", new JsonObject { ["handle"] = handle, ["size"] = ReadChunkBytes }, _sessionId, cancellationToken);
                if (chunk.GetProperty("data").GetString() is { Length: > 0 } data)
                {
                    var bytes = chunk.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean()
                        ? Convert.FromBase64String(data)
                        : System.Text.Encoding.Latin1.GetBytes(data);
                    if (pdf.Length + bytes.Length > MaxPdfBytes)
                    {
                        throw new DevToolsException("The PDF is larger than 100 MB.");
                    }

                    pdf.Write(bytes);
                }

                if (chunk.GetProperty("eof").GetBoolean())
                {
                    break;
                }
            }

            await _pipe.SendAsync("IO.close", new JsonObject { ["handle"] = handle }, _sessionId, cancellationToken);
            return pdf.ToArray();
        }
    }
}
