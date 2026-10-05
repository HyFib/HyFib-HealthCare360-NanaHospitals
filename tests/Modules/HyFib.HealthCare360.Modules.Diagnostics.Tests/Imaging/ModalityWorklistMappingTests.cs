using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Contracts;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Imaging;

/// <summary>A study as a DICOM modality worklist entry: what the modality console shows and stores in the images.</summary>
public sealed class ModalityWorklistMappingTests
{
    private static readonly PatientSnapshot Patient = new(
        Guid.CreateVersion7(), "NH26000001", "Ravi Kumar Sharma", "Ravi", "Kumar", "Sharma", Gender.Male, new DateOnly(1980, 1, 1), null, null, true, false, null);

    private static ImagingStudy Study(string accession = "NHRD260000042") => new()
    {
        FacilityId = Guid.CreateVersion7(),
        PatientId = Patient.Id,
        AccessionNumber = accession,
        SourceOrderNumber = "NHOR260000007",
        ServiceCode = "RAD-CT-HEAD",
        StudyName = "CT brain (plain)",
        Modality = Modality.CT,
        BodyPart = "Head",
        Priority = OrderPriority.Stat,
        OrderedAt = Now.AddHours(-2),
        Status = ImagingStudyStatus.Ordered,
        StudyInstanceUid = "2.25.329800735698586629295641978511506172918",
    };

    [Fact]
    public void Carries_the_patient_the_order_and_the_uid_assigned_at_ordering()
    {
        var study = Study();

        var item = ModalityWorklistMapping.ToItem(study, Patient, "Dr. Meera Rao");

        item.StudyId.ShouldBe(study.Id);
        item.FacilityId.ShouldBe(study.FacilityId);
        item.PatientId.ShouldBe("NH26000001");
        item.PatientName.ShouldBe("Sharma^Ravi^Kumar");
        item.PatientBirthDate.ShouldBe("19800101");
        item.PatientSex.ShouldBe("M");
        item.AccessionNumber.ShouldBe("NHRD260000042");
        item.AccessionFitsDicom.ShouldBeTrue();
        item.RequestedProcedureId.ShouldBe("NHRD260000042");
        item.ScheduledProcedureStepId.ShouldBe("NHRD260000042");
        item.StudyInstanceUid.ShouldBe(study.StudyInstanceUid);
        item.Modality.ShouldBe("CT");
        item.RequestedProcedureDescription.ShouldBe("CT brain (plain)");
        item.BodyPartExamined.ShouldBe("HEAD");
        item.ReferringPhysicianName.ShouldBe("Dr. Meera Rao");
        item.RequestedProcedurePriority.ShouldBe("STAT");
        item.PlacerOrderNumber.ShouldBe("NHOR260000007");
    }

    [Fact]
    public void The_step_starts_on_arrival_else_at_the_appointment_else_when_ordered_in_hospital_time()
    {
        var study = Study();
        ModalityWorklistMapping.ToItem(study, Patient, null).ScheduledStepStartTime.ShouldBe("100000"); // 04:30 UTC = 10:00 IST

        study.Schedule(Now.AddHours(3), "CT-1", "CT1");
        var scheduled = ModalityWorklistMapping.ToItem(study, Patient, null);
        scheduled.ScheduledStepStartDate.ShouldBe("20260925");
        scheduled.ScheduledStepStartTime.ShouldBe("150000");
        scheduled.ScheduledStepStatus.ShouldBe("SCHEDULED");
        scheduled.ScheduledStationAeTitle.ShouldBe("CT1");
        scheduled.ScheduledStepLocation.ShouldBe("CT-1");

        study.Arrive(Now.AddHours(1));
        var arrived = ModalityWorklistMapping.ToItem(study, Patient, null);
        arrived.ScheduledStepStartTime.ShouldBe("130000");
        arrived.ScheduledStepStatus.ShouldBe("ARRIVED");
    }

    [Fact]
    public void An_accession_number_longer_than_dicom_allows_goes_whole_with_procedure_ids_that_fit()
    {
        var study = Study("ABCDEFRD260000042");

        var item = ModalityWorklistMapping.ToItem(study, Patient, null);

        item.AccessionNumber.ShouldBe("ABCDEFRD260000042");
        item.AccessionFitsDicom.ShouldBeFalse();
        item.RequestedProcedureId.ShouldBe(DicomConventions.StudyCode(study.Id));
        item.ScheduledProcedureStepId.ShouldBe(item.RequestedProcedureId);
    }

    [Fact]
    public void Values_that_do_not_fit_their_dicom_type_are_left_out_rather_than_cut()
    {
        var study = Study();
        study.Room = "Radiology block, ground floor, room 4";
        study.BodyPart = "Chest, abdomen and pelvis";

        var item = ModalityWorklistMapping.ToItem(study, Patient, null);

        item.ScheduledStepLocation.ShouldBeNull();
        item.BodyPartExamined.ShouldBeNull();
        item.ReferringPhysicianName.ShouldBeNull();
    }

    [Theory]
    [InlineData(ImagingStudyStatus.Ordered, "SCHEDULED")]
    [InlineData(ImagingStudyStatus.Scheduled, "SCHEDULED")]
    [InlineData(ImagingStudyStatus.Arrived, "ARRIVED")]
    [InlineData(ImagingStudyStatus.InProgress, "STARTED")]
    public void The_step_status_says_how_far_the_patient_has_got(ImagingStudyStatus status, string expected) =>
        ModalityWorklistMapping.StepStatus(status).ShouldBe(expected);
}
