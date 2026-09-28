using HyFib.HealthCare360.Integrations.Pacs;

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
}
