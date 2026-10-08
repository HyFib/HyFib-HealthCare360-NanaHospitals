using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs;

/// <summary>
/// Series and object searches (QIDO-RS) and images (WADO-RS) through the DICOMweb provider: what is asked of the PACS,
/// and what HC360 passes on (JPEG or PNG only, within the size limits).
/// </summary>
public sealed class DicomWebImageTests
{
    private static readonly PacsSettings Settings = new() { Enabled = true, Provider = "dicomweb", QidoUrl = "http://orthanc:8042/dicom-web", TimeZone = "UTC" };
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];

    private readonly ImageHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DicomWebPacsProvider Provider => new(new HttpClient(_http, disposeHandler: false), new PacsStowHttp(new HttpClient(_http, disposeHandler: false)), _tracing.Trace);

    private static PacsImageRequest Rendered(int? frame = null) => new("1.2.3", "1.2.3.1", "1.2.3.1.7", frame, PacsImageKind.Rendered, 1024);

    [Fact]
    public async Task Lists_the_series_of_a_study()
    {
        _http.Respond(HttpStatusCode.OK, DicomJsonTests.SeriesJson, "application/dicom+json");

        var series = await Provider.FindSeriesAsync("1.2.3", Settings, new PacsSecrets(), Ct);

        series.Count.ShouldBe(2);
        _http.Requests.ShouldHaveSingleItem().AbsoluteUri.ShouldStartWith("http://orthanc:8042/dicom-web/studies/1.2.3/series?includefield=");
        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Operation.ShouldBe("qido.series");
        entry.Response.ShouldBe("2 series");
    }

    [Fact]
    public async Task Reads_a_long_series_page_by_page_in_instance_order()
    {
        _http.Respond(HttpStatusCode.OK, Instances(Enumerable.Range(3, 500)), "application/dicom+json")
            .Respond(HttpStatusCode.OK, Instances([2, 1]), "application/dicom+json");

        var instances = await Provider.FindInstancesAsync("1.2.3", "1.2.3.1", Settings, new PacsSecrets(), Ct);

        instances.Count.ShouldBe(502);
        instances.Take(3).Select(i => i.InstanceNumber).ShouldBe([1, 2, 3]);
        _http.Requests.Select(r => r.Query.Contains("offset=0", StringComparison.Ordinal) ? 0 : 500).ShouldBe([0, 500]);
    }

    [Fact]
    public async Task Fetches_rendered_images_from_the_wado_root_and_logs_only_failures()
    {
        _http.Respond(HttpStatusCode.OK, Jpeg, "image/jpeg");

        var image = await Provider.RetrieveImageAsync(Rendered(), Settings with { WadoUrl = "https://pacs.local/wado-rs" }, new PacsSecrets(), Ct);

        image.Status.ShouldBe(PacsImageStatus.Ok);
        image.ContentType.ShouldBe("image/jpeg");
        image.Content.ToArray().ShouldBe(Jpeg);
        _http.Requests.ShouldHaveSingleItem().AbsoluteUri.ShouldBe(
            "https://pacs.local/wado-rs/studies/1.2.3/series/1.2.3.1/instances/1.2.3.1.7/rendered?viewport=1024,1024");
        _http.Accept.ShouldHaveSingleItem().ShouldStartWith("image/jpeg");
        _tracing.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_wado_address_images_come_from_the_dicomweb_root()
    {
        _http.Respond(HttpStatusCode.OK, Png, "image/png");

        var image = await Provider.RetrieveImageAsync(Rendered(frame: 3), Settings, new PacsSecrets(), Ct);

        image.ContentType.ShouldBe("image/png");
        _http.Requests[0].AbsoluteUri.ShouldBe("http://orthanc:8042/dicom-web/studies/1.2.3/series/1.2.3.1/instances/1.2.3.1.7/frames/3/rendered?viewport=1024,1024");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, PacsImageStatus.NotFound)]
    [InlineData(HttpStatusCode.NoContent, PacsImageStatus.NotFound)]
    [InlineData(HttpStatusCode.NotAcceptable, PacsImageStatus.NotAvailable)]
    [InlineData(HttpStatusCode.BadRequest, PacsImageStatus.NotAvailable)]
    [InlineData(HttpStatusCode.NotImplemented, PacsImageStatus.NotAvailable)]
    public async Task Says_when_the_pacs_has_no_such_image(HttpStatusCode status, PacsImageStatus expected)
    {
        _http.Respond(status, [], "text/plain");

        (await Provider.RetrieveImageAsync(Rendered(), Settings, new PacsSecrets(), Ct)).Status.ShouldBe(expected);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("multipart/related")]
    [InlineData("application/dicom")]
    public async Task Passes_on_nothing_but_jpeg_and_png(string contentType)
    {
        _http.Respond(HttpStatusCode.OK, Jpeg, contentType);

        var failure = await Should.ThrowAsync<IntegrationException>(() => Provider.RetrieveImageAsync(Rendered(), Settings, new PacsSecrets(), Ct));

        failure.Message.ShouldContain("instead of a JPEG or PNG image");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }

    [Fact]
    public async Task Refuses_an_image_whose_bytes_are_not_what_it_claims()
    {
        _http.Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), "image/jpeg");

        var failure = await Should.ThrowAsync<IntegrationException>(() => Provider.RetrieveImageAsync(Rendered(), Settings, new PacsSecrets(), Ct));

        failure.Message.ShouldContain("could not recognise");
    }

    [Fact]
    public async Task Refuses_thumbnails_over_the_size_limit()
    {
        var large = Png.Concat(new byte[PacsImageContent.MaxThumbnailBytes]).ToArray();
        _http.Respond(HttpStatusCode.OK, large, "image/png", sendLength: false);

        var failure = await Should.ThrowAsync<IntegrationException>(() =>
            Provider.RetrieveImageAsync(new PacsImageRequest("1.2.3", "1.2.3.1", null, null, PacsImageKind.Thumbnail, 160), Settings, new PacsSecrets(), Ct));

        failure.Message.ShouldContain("larger than 2 MB");
    }

    [Fact]
    public async Task Reads_an_image_of_undeclared_size_up_to_the_limit()
    {
        var image = Png
            .Concat(Enumerable.Range(0, PacsImageContent.MaxThumbnailBytes - Png.Length).Select(i => (byte)i))
            .ToArray();
        _http.Respond(HttpStatusCode.OK, image, "image/png", sendLength: false);

        var thumbnail = new PacsImageRequest("1.2.3", "1.2.3.1", null, null, PacsImageKind.Thumbnail, 160);

        var read = await Provider.RetrieveImageAsync(thumbnail, Settings, new PacsSecrets(), Ct);

        read.Content.Length.ShouldBe(PacsImageContent.MaxThumbnailBytes);
        read.Content.Span.SequenceEqual(image).ShouldBeTrue();
    }

    [Fact]
    public async Task Reads_an_image_of_declared_size_into_one_buffer_of_that_size()
    {
        _http.Respond(HttpStatusCode.OK, Png, "image/png");

        var read = await Provider.RetrieveImageAsync(Rendered(), Settings, new PacsSecrets(), Ct);

        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(read.Content, out var buffer).ShouldBeTrue();
        buffer.Array!.Length.ShouldBe(Png.Length);
    }

    [Fact]
    public async Task Refuses_an_image_shorter_or_longer_than_declared()
    {
        _http.Respond(HttpStatusCode.OK, Png, "image/png", declaredLength: Png.Length + 10L)
            .Respond(HttpStatusCode.OK, Png, "image/png", declaredLength: Png.Length - 1L);

        var shorter = await Should.ThrowAsync<IntegrationException>(
            () => Provider.RetrieveImageAsync(Rendered(), Settings, new PacsSecrets(), Ct));
        var longer = await Should.ThrowAsync<IntegrationException>(
            () => Provider.RetrieveImageAsync(Rendered(), Settings, new PacsSecrets(), Ct));

        shorter.Message.ShouldContain("before its declared length");
        longer.Message.ShouldContain("more image data than it declared");
    }

    [Fact]
    public async Task Refuses_a_declared_length_over_the_limit_before_reading()
    {
        _http.Respond(HttpStatusCode.OK, Png, "image/png", declaredLength: PacsImageContent.MaxRenderedBytes + 1L);

        await Should.ThrowAsync<IntegrationException>(() => Provider.RetrieveImageAsync(Rendered(), Settings, new PacsSecrets(), Ct));
    }

    [Fact]
    public async Task Pacs_errors_for_images_are_failures_with_a_log_entry()
    {
        _http.Respond(HttpStatusCode.Unauthorized, [], "text/plain");

        var failure = await Should.ThrowAsync<IntegrationException>(() => Provider.RetrieveImageAsync(Rendered(), Settings, new PacsSecrets(), Ct));

        failure.Message.ShouldBe("The PACS returned HTTP 401 for an image.");
        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Operation.ShouldBe("wado.rendered");
        entry.Reference.ShouldBe("1.2.3.1");
        entry.Request.ShouldBe("Rendered of one object, 1024 px");
    }

    [Fact]
    public void Recognises_jpeg_and_png_by_their_signature()
    {
        PacsImageContent.Detect(Jpeg).ShouldBe("image/jpeg");
        PacsImageContent.Detect(Png).ShouldBe("image/png");
        PacsImageContent.Detect("GIF89a"u8).ShouldBeNull();
        PacsImageContent.IsAcceptedMediaType("image/jpg").ShouldBeTrue();
        PacsImageContent.IsAcceptedMediaType("image/svg+xml").ShouldBeFalse();
    }

    private static string Instances(IEnumerable<int> numbers) =>
        "[" + string.Join(",", numbers.Select(n => string.Create(CultureInfo.InvariantCulture,
            $$"""{ "00080018": { "vr": "UI", "Value": ["1.2.3.1.{{n}}"] }, "00200013": { "vr": "IS", "Value": [{{n}}] }, "00280010": { "vr": "US", "Value": [512] } }"""))) + "]";

    /// <summary>Answers with queued bodies (text or bytes) and records the address and Accept header of each request.</summary>
    private sealed class ImageHttpHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<Uri> Requests { get; } = [];

        public List<string> Accept { get; } = [];

        public ImageHttpHandler Respond(HttpStatusCode status, string body, string contentType) =>
            Respond(status, Encoding.UTF8.GetBytes(body), contentType);

        public ImageHttpHandler Respond(HttpStatusCode status, byte[] body, string contentType, bool sendLength = true, long? declaredLength = null)
        {
            _responses.Enqueue(() =>
            {
                HttpContent content = sendLength
                    ? new ByteArrayContent(body)
                    : new StreamContent(new UnsizedStream(body));
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                if (declaredLength is { } length)
                {
                    content.Headers.ContentLength = length;
                }

                return new HttpResponseMessage(status) { Content = content };
            });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Accept.Add(request.Headers.Accept.ToString());
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()() : throw new InvalidOperationException("No response queued for " + request.RequestUri));
        }
    }

    /// <summary>A body whose length is not known in advance (as a chunked response).</summary>
    private sealed class UnsizedStream(byte[] body) : MemoryStream(body)
    {
        public override bool CanSeek => false;
    }
}
