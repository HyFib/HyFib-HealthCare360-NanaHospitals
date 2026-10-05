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

    [Theory]
    [InlineData("NHRD260000042", true)]
    [InlineData("ABCDERD26000004", true)]
    [InlineData("ABCDEFRD26000004", true)]
    [InlineData("ABCDEFRD260000042", false)]
    [InlineData("NHRD2610000000", true)]
    [InlineData("ABCDERD2610000000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void An_accession_number_fits_dicom_up_to_16_characters(string? accession, bool fits) =>
        DicomConventions.AccessionFitsDicom(accession).ShouldBe(fits);

    [Fact]
    public void A_procedure_id_is_the_accession_number_when_it_fits()
    {
        var study = Guid.CreateVersion7();

        DicomConventions.ProcedureId("NHRD260000042", study).ShouldBe("NHRD260000042");
    }

    [Fact]
    public void An_accession_number_that_does_not_fit_is_never_cut_short_the_study_gets_its_own_id()
    {
        var study = Guid.CreateVersion7();

        var id = DicomConventions.ProcedureId("ABCDEFRD260000042", study);

        id.ShouldBe(DicomConventions.StudyCode(study));
        id.Length.ShouldBe(16);
        id.All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c, StringComparison.Ordinal)).ShouldBeTrue();
        "ABCDEFRD260000042".ShouldNotStartWith(id);
    }

    [Fact]
    public void Study_codes_are_stable_and_distinct()
    {
        var studies = Enumerable.Range(0, 2000).Select(_ => Guid.CreateVersion7()).ToList();

        studies.Select(DicomConventions.StudyCode).Distinct().Count().ShouldBe(studies.Count);
        DicomConventions.StudyCode(studies[0]).ShouldBe(DicomConventions.StudyCode(studies[0]));
    }

    [Theory]
    [InlineData("CR", new[] { Modality.XR, Modality.DX })]
    [InlineData("dx", new[] { Modality.XR, Modality.DX })]
    [InlineData("XR", new[] { Modality.XR, Modality.DX })]
    [InlineData("CT", new[] { Modality.CT })]
    [InlineData("MG", new[] { Modality.MG })]
    [InlineData("NM", new Modality[0])]
    [InlineData(null, new Modality[0])]
    public void A_modality_code_covers_the_studies_of_that_kind(string? code, Modality[] expected) =>
        DicomConventions.ModalitiesFor(code).ShouldBe(expected);
}
