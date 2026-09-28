using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class EncounterMapperTests
{
    private static readonly Guid VisitId = Guid.Parse("01923a4b-0000-7000-8000-00000000a001");
    private static readonly Guid AdmissionId = Guid.Parse("01923a4b-0000-7000-8000-00000000a002");
    private static readonly Guid EncounterId = Guid.Parse("01923a4b-0000-7000-8000-00000000a003");

    private static VisitRow Visit(VisitType type = VisitType.OutPatient, VisitStatus status = VisitStatus.Completed) => new()
    {
        Id = VisitId,
        PatientId = TestData.PatientId,
        FacilityId = TestData.FacilityId,
        VisitNumber = "NHOP260000042",
        Type = type,
        Status = status,
        DepartmentId = TestData.DepartmentId,
        PractitionerId = TestData.DoctorId,
        VisitedAt = TestData.Now.AddHours(-2),
        CompletedAt = status == VisitStatus.Completed ? TestData.Now : null,
        ChiefComplaint = "Fever for three days",
        CreatedAt = TestData.Now.AddHours(-2),
    };

    private static AdmissionRow Admission(AdmissionStatus status = AdmissionStatus.Discharged, DischargeType? discharge = DischargeType.Normal) => new()
    {
        Id = AdmissionId,
        AdmissionNumber = "NHIP260000007",
        Type = AdmissionType.Emergency,
        Source = AdmissionSource.Emergency,
        Status = status,
        AdmittedAt = TestData.Now.AddDays(-4),
        AdmittingPractitionerId = TestData.DoctorId,
        DischargeType = discharge,
        DischargedAt = status == AdmissionStatus.Discharged ? TestData.Now : null,
        CreatedAt = TestData.Now.AddDays(-4),
        UpdatedAt = TestData.Now.AddMinutes(-5),
    };

    [Fact]
    public void Maps_an_out_patient_visit_as_an_ambulatory_encounter()
    {
        var encounter = EncounterMapper.FromVisit(Visit(), TestData.Directory);

        encounter.Id.ShouldBe($"visit-{VisitId:D}");
        encounter.Status.ShouldBe(R4.Encounter.EncounterStatus.Finished);
        encounter.Class.Code.ShouldBe("AMB");
        encounter.Class.System.ShouldBe("http://terminology.hl7.org/CodeSystem/v3-ActCode");
        encounter.Subject.Reference.ShouldBe($"Patient/{TestData.PatientId:D}");
        encounter.Period.Start.ShouldBe("2026-09-25T08:30:00+05:30");
        encounter.Period.End.ShouldBe("2026-09-25T10:30:00+05:30");
        encounter.Identifier.ShouldHaveSingleItem().Value.ShouldBe("NHOP260000042");
        encounter.ServiceType.Text.ShouldBe("General Medicine");
        encounter.ReasonCode.ShouldHaveSingleItem().Text.ShouldBe("Fever for three days");
        encounter.ServiceProvider.Display.ShouldBe("Nana Hospitals, Pune");
        var participant = encounter.Participant.ShouldHaveSingleItem();
        participant.Individual.Display.ShouldBe("Dr. Asha Rao");
        participant.Type.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("ATND");
    }

    [Theory]
    [InlineData(VisitType.OutPatient, "AMB")]
    [InlineData(VisitType.InPatient, "IMP")]
    [InlineData(VisitType.Emergency, "EMER")]
    [InlineData(VisitType.DayCare, "SS")]
    [InlineData(VisitType.Telehealth, "VR")]
    [InlineData(VisitType.HealthCheckup, "AMB")]
    public void Codes_the_encounter_class_for_every_visit_type(VisitType type, string code) =>
        EncounterMapper.Class(type).Code.ShouldBe(code);

    [Theory]
    [InlineData(VisitStatus.Open, R4.Encounter.EncounterStatus.Arrived)]
    [InlineData(VisitStatus.InProgress, R4.Encounter.EncounterStatus.InProgress)]
    [InlineData(VisitStatus.Completed, R4.Encounter.EncounterStatus.Finished)]
    [InlineData(VisitStatus.Cancelled, R4.Encounter.EncounterStatus.Cancelled)]
    public void Maps_every_visit_status(VisitStatus status, R4.Encounter.EncounterStatus expected) =>
        EncounterMapper.VisitStatus(status).ShouldBe(expected);

    [Fact]
    public void An_open_visit_has_no_period_end() =>
        EncounterMapper.FromVisit(Visit(status: VisitStatus.Open), TestData.Directory).Period.End.ShouldBeNull();

    [Fact]
    public void An_in_patient_visit_carries_its_admission()
    {
        var encounter = EncounterMapper.FromVisit(Visit(VisitType.InPatient, VisitStatus.InProgress) with { Admission = Admission() }, TestData.Directory);

        encounter.Class.Code.ShouldBe("IMP");
        encounter.Status.ShouldBe(R4.Encounter.EncounterStatus.Finished);
        encounter.Period.Start.ShouldBe("2026-09-21T10:30:00+05:30");
        encounter.Period.End.ShouldBe("2026-09-25T10:30:00+05:30");
        encounter.Identifier.ShouldContain(i => i.System == "urn:hc360:admission-number" && i.Value == "NHIP260000007");
        encounter.Hospitalization.AdmitSource.Coding.ShouldHaveSingleItem().Code.ShouldBe("emd");
        encounter.Hospitalization.DischargeDisposition.Coding.ShouldHaveSingleItem().Code.ShouldBe("home");
        encounter.Participant.ShouldContain(p => p.Type[0].Coding[0].Code == "ADM");
        encounter.Meta.LastUpdated.ShouldBe(TestData.Now.AddMinutes(-5));
    }

    [Fact]
    public void An_admission_in_progress_is_in_progress_without_a_discharge_disposition()
    {
        var encounter = EncounterMapper.FromVisit(
            Visit(VisitType.InPatient, VisitStatus.InProgress) with { Admission = Admission(AdmissionStatus.Admitted, null) }, TestData.Directory);

        encounter.Status.ShouldBe(R4.Encounter.EncounterStatus.InProgress);
        encounter.Period.End.ShouldBeNull();
        encounter.Hospitalization.DischargeDisposition.ShouldBeNull();
    }

    [Theory]
    [InlineData(AdmissionStatus.Admitted, R4.Encounter.EncounterStatus.InProgress)]
    [InlineData(AdmissionStatus.DischargeInitiated, R4.Encounter.EncounterStatus.InProgress)]
    [InlineData(AdmissionStatus.Discharged, R4.Encounter.EncounterStatus.Finished)]
    [InlineData(AdmissionStatus.Lama, R4.Encounter.EncounterStatus.Finished)]
    [InlineData(AdmissionStatus.Expired, R4.Encounter.EncounterStatus.Finished)]
    [InlineData(AdmissionStatus.Cancelled, R4.Encounter.EncounterStatus.Cancelled)]
    public void Maps_admission_statuses(AdmissionStatus status, R4.Encounter.EncounterStatus expected) =>
        EncounterMapper.AdmissionStatus(status).ShouldBe(expected);

    [Theory]
    [InlineData(AdmissionSource.OutPatient, AdmissionType.Elective, "outp")]
    [InlineData(AdmissionSource.Emergency, AdmissionType.Emergency, "emd")]
    [InlineData(AdmissionSource.Referral, AdmissionType.Elective, "mp")]
    [InlineData(AdmissionSource.Direct, AdmissionType.Elective, "other")]
    [InlineData(AdmissionSource.Referral, AdmissionType.TransferIn, "hosp-trans")]
    public void Codes_the_admit_source(AdmissionSource source, AdmissionType type, string code) =>
        EncounterMapper.AdmitSource(source, type).Coding.ShouldHaveSingleItem().Code.ShouldBe(code);

    [Theory]
    [InlineData(DischargeType.Normal, "home")]
    [InlineData(DischargeType.Referred, "other-hcf")]
    [InlineData(DischargeType.Lama, "aadvice")]
    [InlineData(DischargeType.Dama, "aadvice")]
    [InlineData(DischargeType.Absconded, "aadvice")]
    [InlineData(DischargeType.Expired, "exp")]
    public void Codes_the_discharge_disposition(DischargeType type, string code) =>
        EncounterMapper.DischargeDisposition(type).Coding.ShouldHaveSingleItem().Code.ShouldBe(code);

    [Fact]
    public void A_clinical_encounter_is_part_of_its_visit()
    {
        var encounter = EncounterMapper.FromClinicalEncounter(new ClinicalEncounterRow
        {
            Id = EncounterId,
            PatientId = TestData.PatientId,
            FacilityId = TestData.FacilityId,
            VisitId = VisitId,
            PractitionerId = TestData.DoctorId,
            Type = EncounterType.OutPatient,
            Status = EncounterStatus.Signed,
            StartedAt = TestData.Now.AddHours(-1),
            SignedAt = TestData.Now,
            CreatedAt = TestData.Now.AddHours(-1),
        }, TestData.Directory);

        encounter.Id.ShouldBe($"enc-{EncounterId:D}");
        encounter.PartOf.Reference.ShouldBe($"Encounter/visit-{VisitId:D}");
        encounter.Status.ShouldBe(R4.Encounter.EncounterStatus.Finished);
        encounter.Period.End.ShouldBe("2026-09-25T10:30:00+05:30");
        encounter.Participant.ShouldHaveSingleItem().Type[0].Coding[0].Code.ShouldBe("PPRF");
    }

    [Fact]
    public void An_in_patient_clinical_encounter_is_part_of_the_admission_visit()
    {
        var directory = new ReferenceDirectory { AdmissionVisits = new Dictionary<Guid, Guid> { [AdmissionId] = VisitId } };

        var encounter = EncounterMapper.FromClinicalEncounter(new ClinicalEncounterRow
        {
            Id = EncounterId,
            PatientId = TestData.PatientId,
            AdmissionId = AdmissionId,
            Type = EncounterType.InPatient,
            Status = EncounterStatus.InProgress,
            StartedAt = TestData.Now,
            CreatedAt = TestData.Now,
        }, directory);

        encounter.PartOf.Reference.ShouldBe($"Encounter/visit-{VisitId:D}");
        encounter.Status.ShouldBe(R4.Encounter.EncounterStatus.InProgress);
        encounter.Period.End.ShouldBeNull();
        encounter.Participant.ShouldBeEmpty();
    }
}
