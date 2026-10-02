using HyFib.HealthCare360.Integrations.Pacs.Worklist;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs.Worklist;

/// <summary>DICOM C-FIND matching for the modality worklist (PS3.4 C.2.2.2): wildcards, ranges, names and modalities.</summary>
public sealed class WorklistMatchingTests
{
    internal static ModalityWorklistItem Item(
        string patientName = "SHARMA^RAVI^KUMAR",
        string patientId = "NH26000001",
        string accession = "NHRD260000042",
        string modality = "CT",
        string? station = null,
        string date = "20261002",
        string time = "103000",
        Guid? facility = null) =>
        new(Guid.NewGuid(), facility ?? Guid.Empty, accession, accession.Length <= 16 ? accession : "HC0000000000000A", accession.Length <= 16 ? accession : "HC0000000000000A",
            "2.25.123456789", patientId, patientName, "19800101", "M", modality, station, date, time, "SCHEDULED", "CT-1", "CT Head plain", "HEAD",
            "Dr Rao", "ROUTINE", "NHOR260000007");

    [Theory]
    [InlineData(null, "anything", true)]
    [InlineData("", "anything", true)]
    [InlineData("*", "anything", true)]
    [InlineData("SHARMA*", "Sharma^Ravi", true)]
    [InlineData("SH?RMA*", "SHARMA^RAVI", true)]
    [InlineData("*RAVI*", "SHARMA^RAVI^KUMAR", true)]
    [InlineData("*RAVI", "SHARMA^RAVI^KUMAR", false)]
    [InlineData("NH26000001", "NH26000001", true)]
    [InlineData("NH2600000?", "NH26000001", true)]
    [InlineData("NH26", "NH26000001", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    public void Wildcards_stand_for_any_run_or_any_one_character(string? pattern, string value, bool matches) =>
        DicomWildcard.IsMatch(pattern, value).ShouldBe(matches);

    [Fact]
    public void A_key_longer_than_any_dicom_value_matches_nothing() =>
        DicomWildcard.IsMatch(new string('A', 65), new string('A', 65)).ShouldBeFalse();

    [Fact]
    public void Many_stars_cannot_make_matching_slow()
    {
        var pattern = string.Concat(Enumerable.Repeat("*A", 30)) + "B";

        DicomWildcard.IsMatch(pattern, new string('A', 64)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("20261002", "20261002", "20261002")]
    [InlineData("20261001-20261003", "20261001", "20261003")]
    [InlineData("20261001-", "20261001", null)]
    [InlineData("-20261003", null, "20261003")]
    public void Reads_dates_and_date_ranges(string value, string? from, string? to) =>
        DicomRange.ParseDate(value).ShouldBe(new DicomRange(from, to));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("2026-10-02")]
    [InlineData("20261302")]
    [InlineData("-")]
    public void Anything_else_is_universal_matching(string? value) => DicomRange.ParseDate(value).ShouldBeNull();

    [Theory]
    [InlineData("0800-1200", "080000", "120059")]
    [InlineData("08-12", "080000", "125959")]
    [InlineData("103000.000", "103000", "103000")]
    [InlineData("10:30", "103000", "103059")]
    public void Time_ranges_cover_the_whole_minute_or_hour_given(string value, string from, string to) =>
        DicomRange.ParseTime(value).ShouldBe(new DicomRange(from, to));

    [Fact]
    public void Names_match_without_regard_to_case_and_by_family_name_alone()
    {
        WorklistMatcher.NameMatches("sharma^ravi^^", "SHARMA^RAVI").ShouldBeTrue();
        WorklistMatcher.NameMatches("SHARMA", "SHARMA^RAVI^KUMAR").ShouldBeTrue();
        WorklistMatcher.NameMatches("RAVI", "SHARMA^RAVI^KUMAR").ShouldBeFalse();
        WorklistMatcher.NameMatches("SHARMA^R*", "SHARMA^RAVI").ShouldBeTrue();
        WorklistMatcher.NameMatches("LAKSHMI DEVI", "LAKSHMI DEVI").ShouldBeTrue();
    }

    [Fact]
    public void An_entry_answers_every_key_or_none()
    {
        var item = Item();

        WorklistMatcher.Matches(item, WorklistKeys.Any).ShouldBeTrue();
        WorklistMatcher.Matches(item, new WorklistKeys(PatientName: "SHAR*", PatientId: "NH26000001", AccessionNumber: "NHRD260000042", Modality: "CT")).ShouldBeTrue();
        WorklistMatcher.Matches(item, new WorklistKeys(Modality: "MR")).ShouldBeFalse();
        WorklistMatcher.Matches(item, new WorklistKeys(PatientId: "NH26000002")).ShouldBeFalse();
        WorklistMatcher.Matches(item, new WorklistKeys(Dates: DicomRange.ParseDate("20261003"))).ShouldBeFalse();
        WorklistMatcher.Matches(item, new WorklistKeys(Dates: DicomRange.ParseDate("20261001-20261002"), Times: DicomRange.ParseTime("1000-1100"))).ShouldBeTrue();
        WorklistMatcher.Matches(item, new WorklistKeys(Times: DicomRange.ParseTime("1100-"))).ShouldBeFalse();
    }

    [Fact]
    public void An_entry_with_no_station_is_offered_to_every_station()
    {
        WorklistMatcher.Matches(Item(station: null), new WorklistKeys(StationAeTitle: "CT1")).ShouldBeTrue();
        WorklistMatcher.Matches(Item(station: "CT1"), new WorklistKeys(StationAeTitle: "ct1")).ShouldBeTrue();
        WorklistMatcher.Matches(Item(station: "CT2"), new WorklistKeys(StationAeTitle: "CT1")).ShouldBeFalse();
    }

    [Fact]
    public void An_accession_number_dicom_cannot_carry_matches_only_universal_keys()
    {
        var item = Item(accession: "ABCDEFRD260000042");

        WorklistMatcher.Matches(item, WorklistKeys.Any).ShouldBeTrue();
        WorklistMatcher.Matches(item, new WorklistKeys(AccessionNumber: "ABCDEFRD26000004")).ShouldBeFalse();
    }

    [Fact]
    public void X_ray_work_reaches_cr_readers_and_dr_rooms_alike()
    {
        WorklistMatcher.ModalityMatches("DX", "CR").ShouldBeTrue();
        WorklistMatcher.ModalityMatches("CR", "DX").ShouldBeTrue();
        WorklistMatcher.ModalityMatches("CT", "CR").ShouldBeFalse();
        WorklistMatcher.ModalityMatches(null, "MR").ShouldBeTrue();
    }

    [Fact]
    public void X_ray_work_is_presented_with_the_code_the_modality_uses()
    {
        WorklistMatcher.PresentedModality("CR", "DX", ["CR", "DX"]).ShouldBe("DX");
        WorklistMatcher.PresentedModality("CR", null, ["DX"]).ShouldBe("DX");
        WorklistMatcher.PresentedModality("DX", null, ["CR"]).ShouldBe("CR");
        WorklistMatcher.PresentedModality("CT", "CT", ["CT"]).ShouldBe("CT");
        WorklistMatcher.PresentedModality("CR", null, ["CT"]).ShouldBe("CR");
    }
}
