using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs;

public sealed class QidoQueryTests
{
    private const string IncludeFields = "includefield=00081030&includefield=00201206&includefield=00201208&includefield=00080061&includefield=00080201";

    [Fact]
    public void Builds_a_study_search_with_every_filter()
    {
        var query = new PacsStudyQuery("UHID00012345", "ACC-2026-0042", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 25), "ct");

        QidoQuery.Build(query, 50).ShouldBe(
            "studies?AccessionNumber=ACC-2026-0042&PatientID=UHID00012345&StudyDate=20260901-20260925&ModalitiesInStudy=CT&" + IncludeFields + "&limit=50");
    }

    [Fact]
    public void Leaves_out_empty_filters() =>
        QidoQuery.Build(new PacsStudyQuery(AccessionNumber: "ACC-1"), 20).ShouldBe("studies?AccessionNumber=ACC-1&" + IncludeFields + "&limit=20");

    [Fact]
    public void Strips_dicom_wildcards_so_one_lookup_never_scans_the_archive()
    {
        var path = QidoQuery.Build(new PacsStudyQuery(PatientId: "*", AccessionNumber: "ACC*42?"), 50);

        path.ShouldStartWith("studies?AccessionNumber=ACC42&includefield=");
        path.ShouldNotContain("PatientID");
    }

    [Fact]
    public void Escapes_query_values() =>
        QidoQuery.Build(new PacsStudyQuery(PatientId: "A&B=C D"), 50).ShouldStartWith("studies?PatientID=A%26B%3DC%20D&");

    [Theory]
    [InlineData(0, "limit=1")]
    [InlineData(10_000, "limit=500")]
    public void Clamps_the_result_limit(int limit, string expected) =>
        QidoQuery.Build(new PacsStudyQuery(AccessionNumber: "ACC-1"), limit).ShouldEndWith(expected);

    [Fact]
    public void Builds_open_ended_date_ranges()
    {
        QidoQuery.DateRange(new DateOnly(2026, 9, 1), null).ShouldBe("20260901-");
        QidoQuery.DateRange(null, new DateOnly(2026, 9, 25)).ShouldBe("-20260925");
        QidoQuery.DateRange(null, null).ShouldBeNull();
    }

    [Fact]
    public void Fills_the_viewer_template()
    {
        QidoQuery.ViewerUrl("https://pacs.local/ohif/viewer?StudyInstanceUIDs={studyUid}", "1.2.840.113619.2.55.3", null)
            .ShouldBe("https://pacs.local/ohif/viewer?StudyInstanceUIDs=1.2.840.113619.2.55.3");
        QidoQuery.ViewerUrl("http://weasis.local/view?acc={ACCESSION}&uid={StudyUid}", "1.2.3", "ACC 42&x")
            .ShouldBe("http://weasis.local/view?acc=ACC%2042%26x&uid=1.2.3");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("javascript:alert(1)//{studyUid}")]
    [InlineData("file:///c:/viewer.exe?{studyUid}")]
    public void Refuses_missing_or_non_web_viewer_templates(string? template) =>
        QidoQuery.ViewerUrl(template, "1.2.3", null).ShouldBeNull();

    [Fact]
    public void A_viewer_link_needs_every_value_its_template_names()
    {
        // Before a study is linked only its accession number is known: an {accession} link already works.
        QidoQuery.ViewerUrl("https://pacs.local/view?acc={accession}", null, "NHRD260000042").ShouldBe("https://pacs.local/view?acc=NHRD260000042");
        QidoQuery.ViewerUrl("https://pacs.local/view?uid={studyUid}", null, "NHRD260000042").ShouldBeNull();
        QidoQuery.ViewerUrl("https://pacs.local/view?acc={accession}&uid={studyUid}", "1.2.3", null).ShouldBeNull();
    }

    [Fact]
    public void A_viewer_link_that_names_no_study_is_not_offered() =>
        QidoQuery.ViewerUrl("https://pacs.local/home", "1.2.3", "ACC-1").ShouldBeNull();

    [Fact]
    public void Searches_one_study_by_its_uid()
    {
        QidoQuery.Build(new PacsStudyQuery(StudyInstanceUid: "2.25.1234"), 10).ShouldStartWith("studies?StudyInstanceUID=2.25.1234&includefield=");

        // A value that is not a UID is never sent.
        QidoQuery.Build(new PacsStudyQuery(AccessionNumber: "ACC-1", StudyInstanceUid: "1.2.*"), 10).ShouldStartWith("studies?AccessionNumber=ACC-1&");
    }

    [Fact]
    public void Lists_the_series_of_a_study_and_pages_through_a_series()
    {
        QidoQuery.Series("1.2.3").ShouldBe("studies/1.2.3/series?includefield=0008103E&includefield=00180015&includefield=00201209&limit=500");
        QidoQuery.Instances("1.2.3", "1.2.3.4", 500).ShouldBe(
            "studies/1.2.3/series/1.2.3.4/instances?includefield=00280008&includefield=00280010&includefield=00280011&includefield=00200013&limit=500&offset=500");
    }

    [Theory]
    [InlineData(null, null, PacsImageKind.Thumbnail, 160, "studies/1.2/series/1.2.3/thumbnail?viewport=160,160")]
    [InlineData("1.2.3.4", null, PacsImageKind.Rendered, 1024, "studies/1.2/series/1.2.3/instances/1.2.3.4/rendered?viewport=1024,1024")]
    [InlineData("1.2.3.4", 7, PacsImageKind.Rendered, 512, "studies/1.2/series/1.2.3/instances/1.2.3.4/frames/7/rendered?viewport=512,512")]
    [InlineData("1.2.3.4", null, PacsImageKind.Thumbnail, 5, "studies/1.2/series/1.2.3/instances/1.2.3.4/thumbnail?viewport=16,16")]
    [InlineData("1.2.3.4", null, PacsImageKind.Rendered, 99_999, "studies/1.2/series/1.2.3/instances/1.2.3.4/rendered?viewport=4096,4096")]
    public void Builds_wado_image_paths(string? instance, int? frame, PacsImageKind kind, int size, string expected) =>
        QidoQuery.Image(new PacsImageRequest("1.2", "1.2.3", instance, frame, kind, size)).ShouldBe(expected);

    [Theory]
    [InlineData("1.2.3/../../patients")]
    [InlineData("1.2.3?x=1")]
    [InlineData("")]
    [InlineData("1..2")]
    public void Refuses_uids_that_could_bend_the_path(string uid) =>
        Should.Throw<IntegrationException>(() => QidoQuery.Image(new PacsImageRequest("1.2", uid, null, null, PacsImageKind.Thumbnail, 100)));

    [Theory]
    [InlineData("1.2.840.10008.5.1.4.31", true)]
    [InlineData("2.25.329800735698586629295641978511506172918", true)]
    [InlineData("1.2.", false)]
    [InlineData("1.2.x", false)]
    [InlineData(null, false)]
    public void Knows_a_dicom_uid(string? uid, bool valid) => QidoQuery.IsValidUid(uid).ShouldBe(valid);
}
