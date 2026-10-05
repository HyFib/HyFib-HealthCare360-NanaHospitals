using System.Buffers.Binary;
using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs;

/// <summary>The training PACS: staff practise linking, the image viewer and thumbnails without a real PACS.</summary>
public sealed class SimulatorPacsProviderTests
{
    private static readonly PacsSettings Settings = new() { Enabled = true, Provider = "simulator" };

    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SimulatorPacsProvider Provider => new(_tracing.Trace, TimeProvider.System);

    [Fact]
    public async Task Finds_a_study_by_accession_number_always_under_the_same_uid()
    {
        var first = (await Provider.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "NHRD260000042"), Settings, new PacsSecrets(), Ct)).ShouldHaveSingleItem();
        var again = (await Provider.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "NHRD260000042"), Settings, new PacsSecrets(), Ct)).ShouldHaveSingleItem();

        first.AccessionNumber.ShouldBe("NHRD260000042");
        again.StudyInstanceUid.ShouldBe(first.StudyInstanceUid);
        QidoQuery.IsValidUid(first.StudyInstanceUid).ShouldBeTrue();
    }

    [Fact]
    public async Task Holds_the_study_under_the_patient_id_hc360_expects()
    {
        var query = new PacsStudyQuery(AccessionNumber: "NHRD260000042", ExpectedPatientId: "NH26000001");

        var study = (await Provider.FindStudiesAsync(query, Settings, new PacsSecrets(), Ct)).ShouldHaveSingleItem();

        study.PatientId.ShouldBe("NH26000001");
    }

    [Fact]
    public async Task Finds_a_study_by_its_uid_when_the_accession_number_cannot_travel()
    {
        var study = (await Provider.FindStudiesAsync(new PacsStudyQuery(StudyInstanceUid: "2.25.12345"), Settings, new PacsSecrets(), Ct)).ShouldHaveSingleItem();

        study.StudyInstanceUid.ShouldBe("2.25.12345");
        study.AccessionNumber.ShouldBeNull();
        (await Provider.FindStudiesAsync(new PacsStudyQuery(), Settings, new PacsSecrets(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_study_has_a_localiser_an_image_series_and_a_cine_loop()
    {
        var series = await Provider.FindSeriesAsync("2.25.12345", Settings, new PacsSecrets(), Ct);
        var cine = await Provider.FindInstancesAsync("2.25.12345", series[2].SeriesInstanceUid, Settings, new PacsSecrets(), Ct);
        var images = await Provider.FindInstancesAsync("2.25.12345", series[1].SeriesInstanceUid, Settings, new PacsSecrets(), Ct);

        series.Select(s => s.Description).ShouldBe(["Localiser", "Images", "Cine loop"]);
        series.ShouldAllBe(s => QidoQuery.IsValidUid(s.SeriesInstanceUid));
        images.Count.ShouldBe(8);
        images.Select(i => i.InstanceNumber).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);
        cine.ShouldHaveSingleItem().Frames.ShouldBe(12);
        (await Provider.FindInstancesAsync("2.25.99", series[1].SeriesInstanceUid, Settings, new PacsSecrets(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Draws_a_png_of_the_size_asked_for_up_to_512_pixels()
    {
        var series = await Provider.FindSeriesAsync("2.25.12345", Settings, new PacsSecrets(), Ct);
        var instance = (await Provider.FindInstancesAsync("2.25.12345", series[1].SeriesInstanceUid, Settings, new PacsSecrets(), Ct))[0];

        var small = await Provider.RetrieveImageAsync(Request(series[1], instance.SopInstanceUid, null, 100), Settings, new PacsSecrets(), Ct);
        var large = await Provider.RetrieveImageAsync(Request(series[1], instance.SopInstanceUid, null, 2048), Settings, new PacsSecrets(), Ct);

        small.Status.ShouldBe(PacsImageStatus.Ok);
        small.ContentType.ShouldBe("image/png");
        PacsImageContent.Detect(small.Content.Span).ShouldBe("image/png");
        Width(small.Content.ToArray()).ShouldBe(100);
        Width(large.Content.ToArray()).ShouldBe(512);
    }

    [Fact]
    public async Task Answers_like_a_pacs_for_missing_frames_and_thumbnails()
    {
        var series = await Provider.FindSeriesAsync("2.25.12345", Settings, new PacsSecrets(), Ct);
        var cine = (await Provider.FindInstancesAsync("2.25.12345", series[2].SeriesInstanceUid, Settings, new PacsSecrets(), Ct))[0];

        (await Provider.RetrieveImageAsync(Request(series[2], cine.SopInstanceUid, 12, 256), Settings, new PacsSecrets(), Ct)).Status.ShouldBe(PacsImageStatus.Ok);
        (await Provider.RetrieveImageAsync(Request(series[2], cine.SopInstanceUid, 13, 256), Settings, new PacsSecrets(), Ct)).Status.ShouldBe(PacsImageStatus.NotFound);
        (await Provider.RetrieveImageAsync(Request(series[2], null, null, 160), Settings, new PacsSecrets(), Ct)).Status.ShouldBe(PacsImageStatus.NotAvailable);
        (await Provider.RetrieveImageAsync(Request(series[0], null, null, 160), Settings, new PacsSecrets(), Ct)).Status.ShouldBe(PacsImageStatus.Ok);
        (await Provider.RetrieveImageAsync(new PacsImageRequest("2.25.12345", "1.2.3", null, null, PacsImageKind.Thumbnail, 160), Settings, new PacsSecrets(), Ct))
            .Status.ShouldBe(PacsImageStatus.NotFound);
    }

    private static PacsImageRequest Request(ImagingSeriesInfo series, string? instance, int? frame, int size) =>
        new("2.25.12345", series.SeriesInstanceUid, instance, frame, instance is null ? PacsImageKind.Thumbnail : PacsImageKind.Rendered, size);

    /// <summary>The width in a PNG's header chunk.</summary>
    private static int Width(byte[] png) => BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
}
