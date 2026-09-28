using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Platform.Contracts;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Imaging;

/// <summary>Radiology workflow: study milestones, PACS reconciliation, report sign-off, release, addenda and AI triage.</summary>
public sealed class ImagingStudyTests
{
    private static readonly AcquisitionDetails Plain = new(false, null, null, "CTDIvol 42 mGy, DLP 780 mGy·cm", null);

    [Fact]
    public void A_study_moves_through_scheduling_arrival_and_acquisition_with_timestamps()
    {
        var study = Study();

        study.Schedule(Now.AddHours(2), " CT room 1 ", " ctaet01 ").IsSuccess.ShouldBeTrue();
        study.Schedule(Now.AddHours(3), null, null).IsSuccess.ShouldBeTrue();
        study.Arrive(Now.AddHours(3)).IsSuccess.ShouldBeTrue();
        study.Start(UserId, Now.AddHours(3.2)).IsSuccess.ShouldBeTrue();
        study.Acquire(Plain, null, Now.AddHours(3.5)).IsSuccess.ShouldBeTrue();

        study.Status.ShouldBe(ImagingStudyStatus.Acquired);
        study.ScheduledAt.ShouldBe(Now.AddHours(3));
        study.Room.ShouldBe("CT room 1");
        study.StationAeTitle.ShouldBe("CTAET01");
        study.ArrivedAt.ShouldBe(Now.AddHours(3));
        study.StartedAt.ShouldBe(Now.AddHours(3.2));
        study.AcquiredAt.ShouldBe(Now.AddHours(3.5));
        study.TechnicianUserId.ShouldBe(UserId);
        study.DoseNote.ShouldBe("CTDIvol 42 mGy, DLP 780 mGy·cm");
    }

    [Fact]
    public void Portable_and_emergency_studies_can_start_without_an_arrival_step()
    {
        var study = Study();

        study.Start(UserId, Now).IsSuccess.ShouldBeTrue();

        study.ArrivedAt.ShouldBe(Now);
        study.Schedule(Now.AddHours(1), null, null).Error!.Code.ShouldBe("imaging.not_schedulable");
        study.Arrive(Now).Error!.Code.ShouldBe("imaging.not_waiting");
    }

    [Fact]
    public void Acquisition_requires_the_patient_on_the_table() =>
        Study(ImagingStudyStatus.Scheduled).Acquire(Plain, UserId, Now).Error!.Code.ShouldBe("imaging.not_in_progress");

    [Fact]
    public void Contrast_studies_must_name_the_agent_and_plain_studies_keep_no_contrast_details()
    {
        var contrast = Study(ImagingStudyStatus.InProgress);
        contrast.Acquire(new AcquisitionDetails(true, " ", 80m, null, null), UserId, Now).Error!.Fields!.Keys.ShouldContain("contrastAgent");

        contrast.Acquire(new AcquisitionDetails(true, " Iohexol 350 ", 80m, null, null), UserId, Now).IsSuccess.ShouldBeTrue();
        contrast.ContrastAgent.ShouldBe("Iohexol 350");
        contrast.ContrastVolumeMl.ShouldBe(80m);

        var plain = Study(ImagingStudyStatus.InProgress);
        plain.Acquire(new AcquisitionDetails(false, "Iohexol 350", 80m, null, null), UserId, Now).IsSuccess.ShouldBeTrue();
        plain.ContrastAgent.ShouldBeNull();
        plain.ContrastVolumeMl.ShouldBeNull();
    }

    [Fact]
    public void Repeated_images_are_recorded_with_a_reason_for_reject_analysis()
    {
        var study = Study(ImagingStudyStatus.InProgress);

        study.RecordRepeat(ImageRepeatReason.PatientMotion, null, Now).IsSuccess.ShouldBeTrue();
        study.RecordRepeat(ImageRepeatReason.Other, " ", Now).Error!.Fields!.Keys.ShouldContain("note");
        Study().RecordRepeat(ImageRepeatReason.Exposure, null, Now).Error!.Code.ShouldBe("imaging.repeat_state");

        var repeat = study.Repeats.ShouldHaveSingleItem();
        repeat.Reason.ShouldBe(ImageRepeatReason.PatientMotion);
        repeat.ImagingStudyId.ShouldBe(study.Id);
    }

    [Fact]
    public void Images_found_in_PACS_complete_a_study_the_radiographer_forgot_to_close()
    {
        var study = Study(ImagingStudyStatus.Arrived);
        const string uid = "1.2.840.113619.2.55.3.604688119.971.1726120000.1";

        study.LinkPacs(uid, 3, 412, Now).IsSuccess.ShouldBeTrue();

        study.Status.ShouldBe(ImagingStudyStatus.Acquired);
        study.StudyInstanceUid.ShouldBe(uid);
        study.SeriesCount.ShouldBe(3);
        study.InstanceCount.ShouldBe(412);
        study.PacsLinkedAt.ShouldBe(Now);
        study.AcquiredAt.ShouldBe(Now);
    }

    [Fact]
    public void Relinking_a_reported_study_updates_the_images_but_not_the_workflow()
    {
        var study = Study(ImagingStudyStatus.Reported);

        study.LinkPacs("1.2.3.4", 4, 500, Now).IsSuccess.ShouldBeTrue();

        study.Status.ShouldBe(ImagingStudyStatus.Reported);
        study.InstanceCount.ShouldBe(500);
        Study(ImagingStudyStatus.Cancelled).LinkPacs("1.2.3.4", 1, 1, Now).Error!.Code.ShouldBe("imaging.cancelled");
    }

    [Theory]
    [InlineData(ImagingStudyStatus.Reported)]
    [InlineData(ImagingStudyStatus.Verified)]
    [InlineData(ImagingStudyStatus.Released)]
    public void A_reported_study_is_never_cancelled(ImagingStudyStatus status) =>
        Study(status).Cancel("Order withdrawn", Now).Error!.Code.ShouldBe("imaging.reported");

    [Fact]
    public void Cancellation_closes_the_study_once()
    {
        var study = Study(ImagingStudyStatus.Scheduled);

        study.Cancel("Patient declined contrast", Now).IsSuccess.ShouldBeTrue();
        study.Cancel("Again", Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        study.IsClosed.ShouldBeTrue();
        study.CancelReason.ShouldBe("Patient declined contrast");
        study.CancelledAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(ImagingStudyStatus.Acquired, true)]
    [InlineData(ImagingStudyStatus.Reported, true)]
    [InlineData(ImagingStudyStatus.Verified, true)]
    [InlineData(ImagingStudyStatus.InProgress, false)]
    [InlineData(ImagingStudyStatus.Released, false)]
    [InlineData(ImagingStudyStatus.Cancelled, false)]
    public void Reports_are_written_after_acquisition_and_before_release(ImagingStudyStatus status, bool reportable) =>
        Study(status).EnsureReportable().IsSuccess.ShouldBe(reportable);

    [Fact]
    public void The_error_names_the_current_state_in_plain_words() =>
        Study(ImagingStudyStatus.InProgress).EnsureReportable().Error!.Message.ShouldContain("in progress");

    [Fact]
    public void Report_milestones_move_the_study_forward_only()
    {
        var study = Study(ImagingStudyStatus.Acquired);

        study.MarkReleased(Now);
        study.Status.ShouldBe(ImagingStudyStatus.Acquired);

        study.MarkVerified(Now);
        study.Status.ShouldBe(ImagingStudyStatus.Verified);
        study.ReportedAt.ShouldBe(Now);

        study.MarkReported(Now.AddHours(1));
        study.Status.ShouldBe(ImagingStudyStatus.Verified);

        study.MarkReleased(Now.AddHours(1));
        study.Status.ShouldBe(ImagingStudyStatus.Released);
        study.IsClosed.ShouldBeTrue();
    }

    [Fact]
    public void A_report_needs_findings_and_an_impression_before_submission()
    {
        var report = Report();

        report.Submit(UserId, Now).Error!.Code.ShouldBe("radiology.report_incomplete");

        report.Save(Content()).IsSuccess.ShouldBeTrue();
        report.Submit(UserId, Now).IsSuccess.ShouldBeTrue();
        report.Status.ShouldBe(DiagnosticReportStatus.Reported);
        report.ReportedBy.ShouldBe(UserId);
        report.Submit(UserId, Now).Error!.Code.ShouldBe("radiology.report_submitted");
    }

    [Fact]
    public void A_critical_finding_must_be_described()
    {
        var report = Report();

        report.Save(Content() with { IsCriticalFinding = true, CriticalFindingNote = null }).Error!.Fields!.Keys.ShouldContain("criticalFindingNote");

        report.Save(Content() with { IsCriticalFinding = false, CriticalFindingNote = "ignored" }).IsSuccess.ShouldBeTrue();
        report.CriticalFindingNote.ShouldBeNull();
    }

    [Fact]
    public void A_radiologist_can_report_and_sign_in_one_step_and_the_text_is_then_frozen()
    {
        var report = Report();
        report.Save(Content()).IsSuccess.ShouldBeTrue();

        report.Verify(UserId, Now).IsSuccess.ShouldBeTrue();

        report.Status.ShouldBe(DiagnosticReportStatus.Verified);
        report.ReportedBy.ShouldBe(UserId);
        report.ReportedAt.ShouldBe(Now);
        report.VerifiedBy.ShouldBe(UserId);
        report.Save(Content()).Error!.Code.ShouldBe("radiology.report_signed");
        report.Verify(UserId, Now).Error!.Code.ShouldBe("radiology.report_signed");
    }

    [Fact]
    public void Release_publishes_the_report_to_the_EHR_with_its_critical_flag()
    {
        var study = Study(ImagingStudyStatus.Verified);
        var report = Report(study);
        report.Save(Content() with { IsCriticalFinding = true, CriticalFindingNote = "Acute subdural haematoma with midline shift" });
        report.Release(study, UserId, Now).Error!.Code.ShouldBe("radiology.report_not_verified");
        report.Verify(UserId, Now);

        report.Release(study, UserId, Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();

        report.Status.ShouldBe(DiagnosticReportStatus.Released);
        VerificationCode.IsWellFormed(report.VerificationCode).ShouldBeTrue();
        study.Status.ShouldBe(ImagingStudyStatus.Released);
        var announced = report.DomainEvents.OfType<DiagnosticReportReleased>().ShouldHaveSingleItem();
        announced.Kind.ShouldBe(DiagnosticKind.Radiology);
        announced.OrderItemId.ShouldBe(study.SourceOrderItemId);
        announced.VisitId.ShouldBe(study.VisitId);
        announced.Title.ShouldBe("CT brain (plain)");
        announced.HasCriticalFindings.ShouldBeTrue();
    }

    [Fact]
    public void Addenda_are_for_signed_reports_and_re_announce_a_released_one()
    {
        var study = Study(ImagingStudyStatus.Verified);
        var report = Report(study);
        report.Save(Content());
        report.AddAddendum(study, "Late comparison", null, false, UserId, Now).Error!.Code.ShouldBe("radiology.report_not_signed");

        report.Verify(UserId, Now);
        report.AddAddendum(study, " Compared with CT of 12 Sep: no change. ", " Prior study received ", false, UserId, Now).IsSuccess.ShouldBeTrue();
        report.DomainEvents.ShouldBeEmpty();

        report.Release(study, UserId, Now);
        report.ClearDomainEvents();
        report.AddAddendum(study, "Small right pneumothorax on the scout image.", null, true, UserId, Now.AddHours(1)).IsSuccess.ShouldBeTrue();

        report.Addenda.Count.ShouldBe(2);
        report.Addenda[0].Text.ShouldBe("Compared with CT of 12 Sep: no change.");
        report.Addenda[0].Reason.ShouldBe("Prior study received");
        var announced = report.DomainEvents.OfType<DiagnosticReportReleased>().ShouldHaveSingleItem();
        announced.Title.ShouldBe("CT brain (plain) (addendum)");
        announced.HasCriticalFindings.ShouldBeTrue();
    }

    [Fact]
    public void An_AI_finding_is_reviewed_once_by_a_radiologist()
    {
        var finding = new ImagingAiFinding { Engine = "qXR", Finding = "Possible right upper lobe consolidation", Confidence = 0.87m, IsUrgent = true };

        finding.Review(acknowledge: true, " Agree, reported ", UserId, Now).IsSuccess.ShouldBeTrue();

        finding.Status.ShouldBe(AiFindingStatus.Acknowledged);
        finding.ReviewNote.ShouldBe("Agree, reported");
        finding.ReviewedBy.ShouldBe(UserId);
        finding.Review(acknowledge: false, null, UserId, Now).Error!.Code.ShouldBe("ai.reviewed");

        var dismissed = new ImagingAiFinding { Engine = "qXR", Finding = "Cardiomegaly" };
        dismissed.Review(acknowledge: false, null, UserId, Now).IsSuccess.ShouldBeTrue();
        dismissed.Status.ShouldBe(AiFindingStatus.Dismissed);
    }

    private static ImagingStudy Study(ImagingStudyStatus status = ImagingStudyStatus.Ordered) => new()
    {
        FacilityId = Guid.CreateVersion7(),
        PatientId = Guid.CreateVersion7(),
        VisitId = Guid.CreateVersion7(),
        SourceOrderItemId = Guid.CreateVersion7(),
        AccessionNumber = "NHRD260000042",
        ServiceCode = "RAD-CT-HEAD",
        StudyName = "CT brain (plain)",
        Modality = Modality.CT,
        BodyPart = "Head",
        OrderedAt = Now.AddHours(-1),
        Status = status,
        StudyInstanceUid = DicomConventions.NewUid(),
    };

    private static RadiologyReport Report(ImagingStudy? study = null)
    {
        study ??= Study(ImagingStudyStatus.Acquired);
        return new RadiologyReport { FacilityId = study.FacilityId, ImagingStudyId = study.Id, PatientId = study.PatientId };
    }

    private static RadiologyReportContent Content() => new(
        null,
        " Axial sections from skull base to vertex without contrast. ",
        "Headache for 3 days",
        null,
        "No intracranial haemorrhage, mass effect or midline shift. Ventricles are normal.",
        " Normal study. ",
        false,
        null);
}
