using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Documents;
using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// The rate list's PDF on the patient portal is made by the browser that makes the PDFs staff print and send, so anyone's
/// downloads have it make each version of the list once, and one at a time.
/// </summary>
public sealed class PublicRateListPdfTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_version_of_the_list_is_made_once_and_a_changed_one_again()
    {
        var renderer = new CountingRenderer();
        var pdfs = new PublicRateListPdf(renderer, new MemoryCache(new MemoryCacheOptions()));

        var first = await pdfs.RenderAsync(Document("Dressing ₹531"), Ct);
        (await pdfs.RenderAsync(Document("Dressing ₹531"), Ct)).ShouldBeSameAs(first);
        renderer.Made.ShouldBe(1);

        (await pdfs.RenderAsync(Document("Dressing ₹590"), Ct)).ShouldNotBeSameAs(first);
        renderer.Made.ShouldBe(2);
    }

    [Fact]
    public async Task Many_asking_at_once_wait_for_the_one_being_made()
    {
        var renderer = new CountingRenderer { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var pdfs = new PublicRateListPdf(renderer, new MemoryCache(new MemoryCacheOptions()));

        var asked = Enumerable.Range(0, 10).Select(_ => pdfs.RenderAsync(Document("Dressing ₹531"), Ct)).ToList();
        await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        renderer.Hold.SetResult();
        var made = await Task.WhenAll(asked);

        renderer.Made.ShouldBe(1);
        made.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_pdf_that_could_not_be_made_is_tried_again_next_time()
    {
        var renderer = new CountingRenderer { FailFirst = true };
        var pdfs = new PublicRateListPdf(renderer, new MemoryCache(new MemoryCacheOptions()));

        await Should.ThrowAsync<PdfRenderException>(() => pdfs.RenderAsync(Document("Dressing ₹531"), Ct));
        (await pdfs.RenderAsync(Document("Dressing ₹531"), Ct)).ShouldNotBeEmpty();

        renderer.Made.ShouldBe(1);
    }

    private static PrintableDocument Document(string line) =>
        new() { Title = "Rate list", FileName = "rate-list-2026-10-29", Body = Html.Of($"<p>{line}</p>") };

    private sealed class CountingRenderer : IPdfRenderer
    {
        private int _made;

        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailFirst { get; set; }

        public int Made => _made;

        public PdfRendererStatus Status => new(true, "test", null);

        public async Task<byte[]> RenderAsync(PdfSource source, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (Hold is not null)
            {
                await Hold.Task.WaitAsync(cancellationToken);
            }

            if (FailFirst)
            {
                FailFirst = false;
                throw new PdfRenderException("No browser on this server.");
            }

            Interlocked.Increment(ref _made);
            return [.. "%PDF-"u8, .. System.Text.Encoding.UTF8.GetBytes(source.Html)];
        }
    }
}
