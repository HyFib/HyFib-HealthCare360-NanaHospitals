using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Integrations.Pdf;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Integrations.Tests.Pdf;

/// <summary>
/// Makes real PDFs with the machine's Microsoft Edge (or Chrome). The CI runners and every Windows 10/11 machine have
/// Edge; elsewhere these tests are skipped.
/// </summary>
public sealed partial class EdgePdfRendererTests : IDisposable
{
    // The space makes the profile folder one that has to be quoted on the browser's command line.
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hc360 pdf tests", Guid.NewGuid().ToString("N")[..8]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Prints_a_document_with_its_running_footer_to_a_pdf()
    {
        await using var renderer = Renderer();
        var rows = Html.Join(Enumerable.Range(1, 120).Select(i => Html.Of($"<tr><td>Test {i}</td><td>{i}</td></tr>")));
        var document = new PrintableDocument
        {
            Title = "Long report",
            FileName = "long",
            Footer = "Asha Rao · UHID NH-0001",
            Body = Html.Of($"<h1>Results</h1><table><thead><tr><th>Test</th><th>Value</th></tr></thead><tbody>{rows}</tbody></table>"),
        };

        var pdf = await renderer.RenderAsync(document.ToPdfSource(), Ct);

        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        Pages(pdf).ShouldBeGreaterThan(1);
        renderer.Status.Available.ShouldBeTrue();
    }

    [Fact]
    public async Task A_document_can_neither_run_scripts_nor_reach_the_network()
    {
        await using var renderer = Renderer();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // The script would make the document several pages long; the image and stylesheet would call the listener.
        const string grow = "for (let i = 0; i < 300; i++) document.body.insertAdjacentHTML('beforeend', '<p>more</p>');";
        var html = $"""
            <!doctype html><html><head><link rel="stylesheet" href="http://127.0.0.1:{port}/style.css"></head>
            <body><p>One page</p><img src="http://127.0.0.1:{port}/logo.png"><script>{grow}</script></body></html>
            """;

        var pdf = await renderer.RenderAsync(new PdfSource(html), Ct);

        Pages(pdf).ShouldBe(1);
        listener.Pending().ShouldBeFalse();
    }

    [Fact]
    public async Task Embedded_images_still_load()
    {
        await using var renderer = Renderer();
        var qr = DocumentAssets.QrCode("https://portal.example.org/portal/verify/lab/CODE");
        var withImage = await renderer.RenderAsync(new PrintableDocument { Title = "QR", FileName = "qr", Body = Html.Image(qr, "QR") }.ToPdfSource(), Ct);
        var without = await renderer.RenderAsync(new PrintableDocument { Title = "QR", FileName = "qr", Body = Html.Empty }.ToPdfSource(), Ct);

        // The QR code's vector paths make the PDF clearly bigger than the same page without it.
        withImage.Length.ShouldBeGreaterThan(without.Length + 1000);
    }

    [Fact]
    public async Task Several_pdfs_can_be_made_at_once()
    {
        await using var renderer = Renderer();

        var pdfs = await Task.WhenAll(Enumerable.Range(1, 5).Select(i =>
            renderer.RenderAsync(new PrintableDocument { Title = $"Copy {i}", FileName = "copy", Body = Html.Of($"<p>Copy {i}</p>") }.ToPdfSource(), Ct)));

        pdfs.ShouldAllBe(pdf => Pages(pdf) == 1);
    }

    [Fact]
    public async Task Closing_the_renderer_ends_the_browser_and_removes_its_profile()
    {
        var renderer = Renderer();
        await renderer.RenderAsync(new PdfSource("<!doctype html><p>x</p>"), Ct);
        var profile = Path.GetFileName(Directory.EnumerateDirectories(Path.Combine(_dataDirectory, "temp", "pdf")).ShouldHaveSingleItem());

        await renderer.DisposeAsync();

        RunningProcessesNaming(profile).ShouldBeEmpty();
        Directory.EnumerateDirectories(Path.Combine(_dataDirectory, "temp", "pdf")).ShouldBeEmpty();
        await Should.ThrowAsync<ObjectDisposedException>(() => renderer.RenderAsync(new PdfSource("<p>x</p>"), Ct));
    }

    [Fact]
    public async Task Without_a_browser_it_says_so()
    {
        await using var renderer = new EdgePdfRenderer(
            Options(Path.Combine(_dataDirectory, "no-such-browser.exe")), Directories(), TimeProvider.System, NullLogger<EdgePdfRenderer>.Instance);

        renderer.Status.Available.ShouldBeFalse();
        renderer.Status.Problem.ShouldNotBeNull().ShouldContain("Edge");
        await Should.ThrowAsync<PdfRenderException>(() => renderer.RenderAsync(new PdfSource("<p>x</p>"), Ct));
    }

    [Fact]
    public void Profiles_left_by_a_stopped_server_are_removed_and_live_ones_kept()
    {
        var root = Path.Combine(_dataDirectory, "profiles");
        var stale = Directory.CreateDirectory(Path.Combine(root, $"{int.MaxValue}-dead")).FullName;
        var live = Directory.CreateDirectory(Path.Combine(root, $"{Environment.ProcessId}-live")).FullName;

        HeadlessBrowser.DeleteLeftoverProfiles(root, NullLogger.Instance);

        Directory.Exists(stale).ShouldBeFalse();
        Directory.Exists(live).ShouldBeTrue();
    }

    private EdgePdfRenderer Renderer()
    {
        Assert.SkipWhen(BrowserLocator.Find(null) is null, "Neither Microsoft Edge nor Google Chrome is installed here.");
        return new EdgePdfRenderer(Options(null), Directories(), TimeProvider.System, new TestOutputLogger<EdgePdfRenderer>());
    }

    // The 30 seconds a PDF may take by default include starting Edge, which a CI runner busy with other tests can make
    // slower than that. These tests check what the PDF holds, not how fast it is made.
    private IOptions<PlatformOptions> Options(string? browserPath) =>
        Microsoft.Extensions.Options.Options.Create(new PlatformOptions
        {
            DataDirectory = _dataDirectory,
            Pdf = new PdfOptions { BrowserPath = browserPath, TimeoutSeconds = 120 },
        });

    private DataDirectories Directories()
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _dataDirectory });
        directories.EnsureCreated();
        return directories;
    }

    private static int Pages(byte[] pdf) => PageObject().Matches(Encoding.Latin1.GetString(pdf)).Count;

    // Edge names the profile folder on the command line of every process it starts. The folder's name is digits, letters
    // and a dash, safe in the query as it is. A process that has exited but is still listed doesn't count.
    private static List<int> RunningProcessesNaming(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var running = new List<int>();
        using var searcher = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE CommandLine LIKE '%{folder}%'");
        foreach (var item in searcher.Get())
        {
            using var entry = (ManagementObject)item;
            var id = Convert.ToInt32(entry["ProcessId"], CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.HasExited)
                {
                    running.Add(id);
                }
            }
            catch (ArgumentException)
            {
                // It has gone.
            }
        }

        return running;
    }

    [GeneratedRegex(@"/Type\s*/Page(?![s\w])")]
    private static partial Regex PageObject();

    /// <summary>The renderer's log in the test's output, which CI shows when the test fails.</summary>
    private sealed class TestOutputLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})")}");
            }
            catch (InvalidOperationException)
            {
                // The test has ended; a late message from the browser's clean-up has nowhere to go.
            }
        }
    }
}
