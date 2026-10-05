using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Imaging;

/// <summary>
/// Whether what PACS answers for an accession number is the study's images: it must be one study, under the patient's
/// UHID. Linking (by hand or on the schedule) and the viewer launch before linking refuse everything else.
/// </summary>
public sealed class PacsStudyMatchTests
{
    private const string Uhid = "NH26000001";

    private static ImagingStudy Study(string accession = "NHRD260000042") => new()
    {
        FacilityId = Guid.CreateVersion7(),
        PatientId = Guid.CreateVersion7(),
        AccessionNumber = accession,
        ServiceCode = "RAD-CT-HEAD",
        StudyName = "CT brain (plain)",
        Modality = Modality.CT,
        Priority = OrderPriority.Routine,
        OrderedAt = Now,
        Status = ImagingStudyStatus.Acquired,
        StudyInstanceUid = "2.25.1000",
    };

    private static ImagingStudyInfo InPacs(
        string? patientId, string uid = "2.25.2000", string? accession = "NHRD260000042") =>
        new(uid, accession, patientId, "SHARMA^RAVI", Now, "CT", "CT brain", 2, 40);

    [Fact]
    public void Links_the_one_study_held_under_the_patients_uhid()
    {
        var match = PacsStudyMatch.Pick([InPacs(" nh26000001 ")], Study(), byAccession: true, Uhid);

        match.IsSuccess.ShouldBeTrue();
        match.Value.StudyInstanceUid.ShouldBe("2.25.2000");
    }

    [Fact]
    public void Refuses_a_study_under_another_patient()
    {
        var match = PacsStudyMatch.Pick([InPacs("NH26000999")], Study(), byAccession: true, Uhid);

        match.Error!.Code.ShouldBe(PacsStudyMatch.PatientMismatch);
        match.Error.Message.ShouldContain("NH26000999");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Refuses_a_study_without_a_patient_id(string? patientId)
    {
        var match = PacsStudyMatch.Pick([InPacs(patientId)], Study(), byAccession: true, Uhid);

        match.Error!.Code.ShouldBe(PacsStudyMatch.PatientIdMissing);
        PacsStudyMatch.IsPatientSafety(match.Error.Code).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Refuses_when_the_patients_uhid_is_unknown(string? uhid)
    {
        var match = PacsStudyMatch.Pick([InPacs(Uhid)], Study(), byAccession: true, uhid);

        match.Error!.Code.ShouldBe(PacsStudyMatch.PatientUnknown);
    }

    [Fact]
    public void Refuses_several_studies_under_one_accession_even_when_one_is_the_patients()
    {
        ImagingStudyInfo[] found = [InPacs(Uhid), InPacs("NH26000999", "2.25.3000")];

        var match = PacsStudyMatch.Pick(found, Study(), byAccession: true, Uhid);

        match.Error!.Code.ShouldBe(PacsStudyMatch.Ambiguous);
        match.Error.Message.ShouldContain("2 studies");
    }

    [Fact]
    public void The_same_study_listed_twice_is_one_study()
    {
        var match = PacsStudyMatch.Pick([InPacs(Uhid), InPacs(Uhid)], Study(), byAccession: true, Uhid);

        match.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Ignores_studies_with_another_accession_and_reports_none_found()
    {
        var match = PacsStudyMatch.Pick([InPacs(Uhid, accession: "NHRD260000043")], Study(), byAccession: true, Uhid);

        match.Error!.Code.ShouldBe(PacsStudyMatch.NotFound);
        PacsStudyMatch.IsPatientSafety(match.Error.Code).ShouldBeFalse();
    }

    [Fact]
    public void By_uid_the_study_must_carry_the_uid_hc360_assigned_and_the_uhid()
    {
        var study = Study("NHRDXYZT2600000042");

        PacsStudyMatch.Pick([InPacs(Uhid, "2.25.1000", null)], study, byAccession: false, Uhid).IsSuccess
            .ShouldBeTrue();
        PacsStudyMatch.Pick([InPacs(null, "2.25.1000", null)], study, byAccession: false, Uhid).Error!.Code
            .ShouldBe(PacsStudyMatch.PatientIdMissing);
        PacsStudyMatch.Pick([InPacs(Uhid, "2.25.9999", null)], study, byAccession: false, Uhid).Error!.Code
            .ShouldBe(PacsStudyMatch.NotFound);
    }
}
