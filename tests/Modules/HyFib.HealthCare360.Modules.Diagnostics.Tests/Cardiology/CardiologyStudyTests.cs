using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Platform.Contracts;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Cardiology;

/// <summary>Cardiology workflow: device output before completion, structured report, sign-off and release.</summary>
public sealed class CardiologyStudyTests
{
    [Theory]
    [InlineData("CARD-ECG", "Electrocardiogram (12-lead ECG)", CardiologyStudyType.Ecg)]
    [InlineData("CARD-EKG-RHYTHM", "Rhythm strip", CardiologyStudyType.Ecg)]
    [InlineData("CARD-ECHO", "2D echocardiography with colour Doppler", CardiologyStudyType.Echo)]
    [InlineData("CARD-TMT", "Treadmill test (TMT / exercise stress test)", CardiologyStudyType.Tmt)]
    [InlineData("CARD-STRESS", "Exercise stress test", CardiologyStudyType.Tmt)]
    [InlineData("CARD-HOLTER", "Holter monitoring (24 hours)", CardiologyStudyType.Holter)]
    [InlineData("CARD-ABPM", "Ambulatory blood pressure monitoring (24 hours)", CardiologyStudyType.Abpm)]
    [InlineData("CARD-TILT", "Head-up tilt table test", CardiologyStudyType.Other)]
    public void The_study_type_is_read_from_the_service_code_and_name(string code, string name, CardiologyStudyType expected) =>
        CardiologyStudyTypes.Resolve(code, name).ShouldBe(expected);

    [Fact]
    public void Holter_ECG_is_a_Holter_study_not_a_resting_ECG() =>
        CardiologyStudyTypes.Resolve("CARD-HOLTER-ECG", "24 hour Holter ECG").ShouldBe(CardiologyStudyType.Holter);

    [Fact]
    public void A_study_cannot_be_completed_without_its_tracing()
    {
        var study = Study();
        study.Start(UserId, " MAC 2000 ", Now).IsSuccess.ShouldBeTrue();

        study.Complete(UserId, null, Now).Error!.Code.ShouldBe("cardiology.no_tracing");

        var tracing = Guid.CreateVersion7();
        study.AttachFiles([tracing, tracing]).IsSuccess.ShouldBeTrue();
        study.Complete(null, " Patient anxious; repeat strip attached ", Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();

        study.Status.ShouldBe(CardiologyStudyStatus.Acquired);
        study.DeviceName.ShouldBe("MAC 2000");
        study.DeviceFileIds.ShouldHaveSingleItem().ShouldBe(tracing);
        study.TechnicianUserId.ShouldBe(UserId);
        study.TechnicianNotes.ShouldBe("Patient anxious; repeat strip attached");
        study.AcquiredAt.ShouldBe(Now.AddMinutes(10));
    }

    [Fact]
    public void A_walk_in_ECG_is_completed_straight_from_the_order()
    {
        var study = Study();
        study.AttachFiles([Guid.CreateVersion7()]);

        study.Complete(UserId, null, Now).IsSuccess.ShouldBeTrue();

        study.StartedAt.ShouldBe(Now);
        study.Start(UserId, null, Now).Error!.Code.ShouldBe("cardiology.not_startable");
        study.Schedule(Now.AddDays(1), null).Error!.Code.ShouldBe("cardiology.not_schedulable");
        study.Complete(UserId, null, Now).Error!.Code.ShouldBe("cardiology.not_in_progress");
    }

    [Fact]
    public void Scheduling_keeps_the_room_until_a_new_one_is_given()
    {
        var study = Study();

        study.Schedule(Now.AddHours(1), " Echo lab 2 ").IsSuccess.ShouldBeTrue();
        study.Schedule(Now.AddHours(2), null).IsSuccess.ShouldBeTrue();

        study.Room.ShouldBe("Echo lab 2");
        study.ScheduledAt.ShouldBe(Now.AddHours(2));
    }

    [Theory]
    [InlineData(CardiologyStudyStatus.Verified)]
    [InlineData(CardiologyStudyStatus.Released)]
    [InlineData(CardiologyStudyStatus.Cancelled)]
    public void Files_cannot_be_added_after_sign_off(CardiologyStudyStatus status) =>
        Study(status).AttachFiles([Guid.CreateVersion7()]).Error!.Code.ShouldBe("cardiology.closed");

    [Fact]
    public void A_reported_study_is_never_cancelled()
    {
        Study(CardiologyStudyStatus.Reported).Cancel("Not needed", Now).Error!.Code.ShouldBe("cardiology.reported");

        var study = Study(CardiologyStudyStatus.Scheduled);
        study.Cancel("Patient did not come", Now).IsSuccess.ShouldBeTrue();
        study.Status.ShouldBe(CardiologyStudyStatus.Cancelled);
        study.CancelReason.ShouldBe("Patient did not come");
    }

    [Theory]
    [InlineData(CardiologyStudyStatus.Acquired, true)]
    [InlineData(CardiologyStudyStatus.Reported, true)]
    [InlineData(CardiologyStudyStatus.InProgress, false)]
    [InlineData(CardiologyStudyStatus.Verified, false)]
    public void Reports_are_written_after_the_device_output_is_in(CardiologyStudyStatus status, bool reportable) =>
        Study(status).EnsureReportable().IsSuccess.ShouldBe(reportable);

    [Fact]
    public void Saving_keeps_measurements_of_other_study_types_and_trims_text()
    {
        var report = Report();
        report.Save(Content() with { Ecg = new EcgFindings { HeartRate = 72, QtcInterval = 430 } }).IsSuccess.ShouldBeTrue();

        report.Save(Content() with { Conclusion = "  Normal sinus rhythm.  ", Recommendations = " " }).IsSuccess.ShouldBeTrue();

        report.Ecg.HeartRate.ShouldBe(72);
        report.Ecg.QtcInterval.ShouldBe(430);
        report.Conclusion.ShouldBe("Normal sinus rhythm.");
        report.Recommendations.ShouldBeNull();
    }

    [Fact]
    public void A_critical_finding_needs_a_description()
    {
        var report = Report();

        report.Save(Content() with { IsCriticalFinding = true }).Error!.Fields!.Keys.ShouldContain("criticalFindingNote");
        report.Save(Content() with { IsCriticalFinding = true, CriticalFindingNote = " ST elevation V1-V4: acute anterior STEMI " })
            .IsSuccess.ShouldBeTrue();

        report.CriticalFindingNote.ShouldBe("ST elevation V1-V4: acute anterior STEMI");
    }

    [Fact]
    public void Submission_and_sign_off_need_a_conclusion()
    {
        var report = Report();
        report.Submit(UserId, Now).Error!.Fields!.Keys.ShouldContain("conclusion");
        report.Verify(UserId, Now).Error!.Fields!.Keys.ShouldContain("conclusion");

        report.Save(Content());
        report.Submit(UserId, Now).IsSuccess.ShouldBeTrue();
        report.Submit(UserId, Now).Error!.Code.ShouldBe("cardiology.report_submitted");

        var cardiologist = Guid.CreateVersion7();
        report.Verify(cardiologist, Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        report.ReportedBy.ShouldBe(UserId);
        report.VerifiedBy.ShouldBe(cardiologist);
        report.Save(Content()).Error!.Code.ShouldBe("cardiology.report_signed");
        report.Verify(cardiologist, Now).Error!.Code.ShouldBe("cardiology.report_signed");
    }

    [Fact]
    public void Release_publishes_the_report_and_closes_the_study()
    {
        var study = Study(CardiologyStudyStatus.Verified);
        var report = Report(study);
        report.Save(Content() with { Echo = new EchoFindings { Lvef = 35m, Rwma = "Anterior wall hypokinesia" } });
        report.Release(study, UserId, Now).Error!.Code.ShouldBe("cardiology.report_not_verified");
        report.Verify(UserId, Now);

        report.Release(study, UserId, Now).IsSuccess.ShouldBeTrue();

        study.Status.ShouldBe(CardiologyStudyStatus.Released);
        VerificationCode.IsWellFormed(report.VerificationCode).ShouldBeTrue();
        var announced = report.DomainEvents.OfType<DiagnosticReportReleased>().ShouldHaveSingleItem();
        announced.Kind.ShouldBe(DiagnosticKind.Cardiology);
        announced.OrderItemId.ShouldBe(study.SourceOrderItemId);
        announced.Title.ShouldBe("2D echocardiography with colour Doppler");
        announced.HasCriticalFindings.ShouldBeFalse();
    }

    [Fact]
    public void Report_milestones_move_the_study_forward_only()
    {
        var study = Study(CardiologyStudyStatus.Acquired);

        study.MarkReported(Now);
        study.Status.ShouldBe(CardiologyStudyStatus.Reported);
        study.MarkVerified(Now.AddHours(1));
        study.Status.ShouldBe(CardiologyStudyStatus.Verified);
        study.ReportedAt.ShouldBe(Now);
        study.MarkReported(Now.AddHours(2));
        study.Status.ShouldBe(CardiologyStudyStatus.Verified);
    }

    private static CardiologyStudy Study(CardiologyStudyStatus status = CardiologyStudyStatus.Ordered) => new()
    {
        FacilityId = Guid.CreateVersion7(),
        PatientId = Guid.CreateVersion7(),
        VisitId = Guid.CreateVersion7(),
        SourceOrderItemId = Guid.CreateVersion7(),
        AccessionNumber = "NHCD260000007",
        ServiceCode = "CARD-ECHO",
        StudyName = "2D echocardiography with colour Doppler",
        StudyType = CardiologyStudyType.Echo,
        OrderedAt = Now.AddHours(-2),
        Status = status,
    };

    private static CardiologyReport Report(CardiologyStudy? study = null)
    {
        study ??= Study(CardiologyStudyStatus.Acquired);
        return new CardiologyReport
        {
            FacilityId = study.FacilityId,
            CardiologyStudyId = study.Id,
            PatientId = study.PatientId,
            StudyType = study.StudyType,
        };
    }

    private static CardiologyReportContent Content() =>
        new(null, null, null, null, null, "Normal study.", "Routine follow-up.", false, null);
}
