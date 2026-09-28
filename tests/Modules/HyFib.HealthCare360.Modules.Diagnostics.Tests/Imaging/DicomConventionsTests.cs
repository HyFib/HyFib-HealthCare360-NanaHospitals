using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Imaging;

/// <summary>DICOM value formatting for the modality worklist and PACS reconciliation.</summary>
public sealed class DicomConventionsTests
{
    [Fact]
    public void A_UUID_becomes_a_2_25_UID_as_in_the_ITU_T_X667_example() =>
        DicomConventions.UidFrom(Guid.Parse("f81d4fae-7dec-11d0-a765-00a0c91e6bf6"))
            .ShouldBe("2.25.329800735698586629295641978511506172918");

    [Fact]
    public void New_UIDs_are_valid_unique_and_fit_the_64_character_limit()
    {
        var uids = Enumerable.Range(0, 200).Select(_ => DicomConventions.NewUid()).ToList();

        uids.ShouldAllBe(uid => uid.StartsWith(DicomConventions.UuidRoot, StringComparison.Ordinal) && DicomConventions.IsValidUid(uid));
        uids.ShouldAllBe(uid => uid.Length <= DicomConventions.MaxUidLength);
        uids.Distinct().Count().ShouldBe(uids.Count);
    }

    [Theory]
    [InlineData("1.2.840.10008.5.1.4.31", true)]
    [InlineData("2.25.0", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(".1.2", false)]
    [InlineData("1.2.", false)]
    [InlineData("1..2", false)]
    [InlineData("1.2.a", false)]
    [InlineData("1.2 .3", false)]
    public void UIDs_are_dot_separated_digits(string? uid, bool valid) =>
        DicomConventions.IsValidUid(uid).ShouldBe(valid);

    [Fact]
    public void A_UID_longer_than_64_characters_is_invalid() =>
        DicomConventions.IsValidUid("1." + new string('2', 63)).ShouldBeFalse();

    [Theory]
    [InlineData("Ravi", "Kumar", "Sharma", "Sharma^Ravi^Kumar")]
    [InlineData("Ravi", null, "Sharma", "Sharma^Ravi")]
    [InlineData("Lakshmi", null, null, "Lakshmi")]
    [InlineData("Lakshmi", "Devi", " ", "Lakshmi Devi")]
    [InlineData(" Ra^vi ", null, "Sha=rma\\", "Sharma^Ravi")]
    public void Names_are_written_family_first_and_mononyms_stay_whole(string first, string? middle, string? last, string expected) =>
        DicomConventions.PersonName(first, middle, last).ShouldBe(expected);

    [Fact]
    public void Dates_times_sex_and_modality_use_DICOM_formats()
    {
        DicomConventions.Date(new DateOnly(2026, 9, 5)).ShouldBe("20260905");
        DicomConventions.Time(new TimeOnly(9, 5, 7)).ShouldBe("090507");
        DicomConventions.Sex(Gender.Male).ShouldBe("M");
        DicomConventions.Sex(Gender.Female).ShouldBe("F");
        DicomConventions.Sex(Gender.Other).ShouldBe("O");
        DicomConventions.Sex(Gender.Unknown).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData(Modality.XR, "CR")]
    [InlineData(Modality.DX, "DX")]
    [InlineData(Modality.CT, "CT")]
    [InlineData(Modality.MR, "MR")]
    [InlineData(Modality.US, "US")]
    [InlineData(Modality.MG, "MG")]
    [InlineData(Modality.RF, "RF")]
    public void Conventional_radiography_is_sent_to_the_worklist_as_CR(Modality modality, string code) =>
        DicomConventions.ModalityCode(modality).ShouldBe(code);
}
