using System.Text;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The server's own PDF renderer, except for a request that asks otherwise in <see cref="Header"/>: <see cref="Made"/>
/// makes the PDF here from the page, the same bytes for the same page and with the page readable in them; <see cref="Fails"/>
/// fails as a server without a browser does. A test of what happens to a PDF once it is made (a record copy's hashes,
/// RecordCopyTests) then runs alike on CI's Windows runner, which has Edge, and on a machine with none. Every other
/// request, and every test that sends no header, gets the server's renderer.
/// </summary>
internal sealed class TestPdfRenderer(IPdfRenderer server, IHttpContextAccessor http) : IPdfRenderer, IAsyncDisposable
{
    public const string Header = "X-HC360-Test-Pdf";
    public const string Made = "made";
    public const string Fails = "fails";

    /// <summary>The request's first PDF is made as <see cref="Made"/>; every later one is larger than the file store keeps.</summary>
    public const string TooLargeAfterTheFirst = "too-large-after-the-first";

    /// <summary>What a test PDF starts with: a PDF header, so the file store takes it as one.</summary>
    public static readonly byte[] Preamble = "%PDF-1.7\n% HC360 test PDF: the page follows\n"u8.ToArray();

    public PdfRendererStatus Status => Mode switch
    {
        Made or TooLargeAfterTheFirst => new PdfRendererStatus(true, "test", null),
        Fails => new PdfRendererStatus(false, null, "No browser on this test server."),
        _ => server.Status,
    };

    public Task<byte[]> RenderAsync(PdfSource source, CancellationToken cancellationToken) => Mode switch
    {
        Made => Task.FromResult(Page(source)),
        TooLargeAfterTheFirst => Task.FromResult(First() ? Page(source) : TooLarge()),
        Fails => Task.FromException<byte[]>(new PdfRenderException("No browser on this test server, so PDFs can't be made.")),
        _ => server.RenderAsync(source, cancellationToken),
    };

    /// <summary>The page a test PDF was made from.</summary>
    public static string PageOf(byte[] pdf) =>
        pdf.AsSpan().StartsWith(Preamble) ? Encoding.UTF8.GetString(pdf, Preamble.Length, pdf.Length - Preamble.Length) : throw new InvalidOperationException("Not a test PDF.");

    public async ValueTask DisposeAsync()
    {
        if (server is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    private string? Mode => http.HttpContext?.Request.Headers[Header].ToString() is { Length: > 0 } mode ? mode : null;

    private static byte[] Page(PdfSource source) => [.. Preamble, .. Encoding.UTF8.GetBytes(source.Html)];

    // A PDF one byte over the file store's limit.
    private static byte[] TooLarge()
    {
        var pdf = new byte[EncryptedFileStore.MaxBytes + 1];
        Preamble.CopyTo(pdf, 0);
        return pdf;
    }

    // Whether this is the request's first PDF.
    private bool First()
    {
        var items = http.HttpContext!.Items;
        var first = !items.ContainsKey(Header);
        items[Header] = true;
        return first;
    }
}
