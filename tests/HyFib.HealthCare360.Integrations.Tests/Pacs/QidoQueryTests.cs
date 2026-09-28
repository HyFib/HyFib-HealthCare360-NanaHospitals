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
}
