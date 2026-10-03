using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs;

public sealed class DicomJsonTests
{
    /// <summary>A QIDO-RS response as Orthanc/dcm4chee return it (PS3.18 Annex F DICOM JSON).</summary>
    internal const string StudiesJson = """
        [
          {
            "00080020": { "vr": "DA", "Value": ["20260925"] },
            "00080030": { "vr": "TM", "Value": ["143015.123"] },
            "00080050": { "vr": "SH", "Value": ["ACC-2026-0042"] },
            "00080061": { "vr": "CS", "Value": ["CT", "SR"] },
            "00081030": { "vr": "LO", "Value": ["CT BRAIN PLAIN"] },
            "00100010": { "vr": "PN", "Value": [{ "Alphabetic": "SHARMA^RAVI^^^" }] },
            "00100020": { "vr": "LO", "Value": ["UHID00012345"] },
            "0020000D": { "vr": "UI", "Value": ["1.2.840.113619.2.55.3.604688119.868.1758790215.100"] },
            "00201206": { "vr": "IS", "Value": [3] },
            "00201208": { "vr": "IS", "Value": ["245"] }
          },
          {
            "0020000d": { "vr": "UI", "Value": ["1.2.3.4"] },
            "00080020": { "vr": "DA", "Value": ["20260924"] },
            "00080030": { "vr": "TM", "Value": ["09:05"] },
            "00080201": { "vr": "SH", "Value": ["+0000"] },
            "00100010": { "vr": "PN", "Value": [{ "Alphabetic": "YAMADA^TARO", "Ideographic": "山田^太郎" }] }
          },
          { "00080050": { "vr": "SH", "Value": ["NO-UID"] } }
        ]
        """;

    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");

    [Fact]
    public void Reads_study_attributes_by_tag()
    {
        var studies = DicomJson.ParseStudies(StudiesJson, India);

        studies.Count.ShouldBe(2);
        var study = studies[0];
        study.StudyInstanceUid.ShouldBe("1.2.840.113619.2.55.3.604688119.868.1758790215.100");
        study.AccessionNumber.ShouldBe("ACC-2026-0042");
        study.PatientId.ShouldBe("UHID00012345");
        study.PatientName.ShouldBe("SHARMA RAVI");
        study.Modalities.ShouldBe("CT,SR");
        study.Description.ShouldBe("CT BRAIN PLAIN");
        study.SeriesCount.ShouldBe(3);
        study.InstanceCount.ShouldBe(245);
    }

    [Fact]
    public void Study_times_use_the_pacs_zone_unless_an_offset_is_given()
    {
        var studies = DicomJson.ParseStudies(StudiesJson, India);

        studies[0].StudyDate.ShouldBe(new DateTimeOffset(2026, 9, 25, 14, 30, 15, TimeSpan.FromHours(5.5)));
        studies[1].StudyDate.ShouldBe(new DateTimeOffset(2026, 9, 24, 9, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Tolerates_lowercase_tags_and_missing_optional_attributes()
    {
        var study = DicomJson.ParseStudies(StudiesJson, India)[1];

        study.StudyInstanceUid.ShouldBe("1.2.3.4");
        study.PatientName.ShouldBe("YAMADA TARO");
        study.AccessionNumber.ShouldBeNull();
        study.Modalities.ShouldBeNull();
        study.SeriesCount.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"error":"unauthorised"}""")]
    [InlineData("[]")]
    public void Unexpected_answers_yield_no_studies(string json) =>
        DicomJson.ParseStudies(json, India).ShouldBeEmpty();

    [Fact]
    public void A_study_date_without_time_is_midnight_local()
    {
        const string json = """[{ "0020000D": { "vr": "UI", "Value": ["1.2"] }, "00080020": { "vr": "DA", "Value": ["20260901"] } }]""";

        DicomJson.ParseStudies(json, India).ShouldHaveSingleItem().StudyDate.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(5.5)));
    }

    [Fact]
    public void Invalid_dates_are_left_empty()
    {
        const string json = """[{ "0020000D": { "vr": "UI", "Value": ["1.2"] }, "00080020": { "vr": "DA", "Value": ["2026-09-01"] } }]""";

        DicomJson.ParseStudies(json, India).ShouldHaveSingleItem().StudyDate.ShouldBeNull();
    }

    /// <summary>A QIDO-RS series search: an image series, a scout, a structured report and an entry without a UID.</summary>
    internal const string SeriesJson = """
        [
          { "0020000E": { "vr": "UI", "Value": ["1.2.3.2"] }, "00080060": { "vr": "CS", "Value": ["CT"] }, "00200011": { "vr": "IS", "Value": [2] },
            "0008103E": { "vr": "LO", "Value": ["Axial 5 mm"] }, "00201209": { "vr": "IS", "Value": ["120"] } },
          { "0020000E": { "vr": "UI", "Value": ["1.2.3.1"] }, "00080060": { "vr": "CS", "Value": ["CT"] }, "00200011": { "vr": "IS", "Value": [1] },
            "0008103E": { "vr": "LO", "Value": ["Scout"] }, "00180015": { "vr": "CS", "Value": ["HEAD"] }, "00201209": { "vr": "IS", "Value": [2] } },
          { "0020000E": { "vr": "UI", "Value": ["1.2.3.9"] }, "00080060": { "vr": "CS", "Value": ["SR"] }, "00200011": { "vr": "IS", "Value": [99] } },
          { "00080060": { "vr": "CS", "Value": ["CT"] } }
        ]
        """;

    [Fact]
    public void Lists_picture_series_by_series_number()
    {
        var series = DicomJson.ParseSeries(SeriesJson);

        series.Select(s => s.SeriesInstanceUid).ShouldBe(["1.2.3.1", "1.2.3.2"]);
        series[0].ShouldBe(new ImagingSeriesInfo("1.2.3.1", "CT", 1, "Scout", "HEAD", 2));
        series[1].InstanceCount.ShouldBe(120);
    }

    [Fact]
    public void Lists_the_pictures_of_a_series_with_their_frames()
    {
        const string json = """
            [
              { "00080018": { "vr": "UI", "Value": ["1.2.3.2.2"] }, "00080016": { "vr": "UI", "Value": ["1.2.840.10008.5.1.4.1.1.2"] },
                "00200013": { "vr": "IS", "Value": [2] }, "00280010": { "vr": "US", "Value": [512] }, "00280011": { "vr": "US", "Value": [512] } },
              { "00080018": { "vr": "UI", "Value": ["1.2.3.2.1"] }, "00080016": { "vr": "UI", "Value": ["1.2.840.10008.5.1.4.1.1.6.1"] },
                "00200013": { "vr": "IS", "Value": ["1"] }, "00280008": { "vr": "IS", "Value": ["30"] }, "00280010": { "vr": "US", "Value": [480] } },
              { "00080018": { "vr": "UI", "Value": ["1.2.3.2.8"] }, "00080016": { "vr": "UI", "Value": ["1.2.840.10008.5.1.4.1.1.88.11"] } },
              { "00080018": { "vr": "UI", "Value": ["1.2.3.2.9"] }, "00280010": { "vr": "US", "Value": [0] } }
            ]
            """;

        var instances = DicomJson.ParseInstances(json);

        instances.Select(i => i.SopInstanceUid).ShouldBe(["1.2.3.2.2", "1.2.3.2.1"]);
        instances[0].Frames.ShouldBe(1);
        instances[1].ShouldBe(new ImagingInstanceInfo("1.2.3.2.1", "1.2.840.10008.5.1.4.1.1.6.1", 1, 30, 480, null));
    }
}
