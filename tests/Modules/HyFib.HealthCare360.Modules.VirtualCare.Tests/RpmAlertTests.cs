using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class RpmAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 4, 30, 0, TimeSpan.Zero);

    private static readonly RpmEnrollment Enrollment = new()
    {
        FacilityId = Guid.CreateVersion7(),
        PatientId = Guid.CreateVersion7(),
        Status = RpmEnrollmentStatus.Active,
        StartDate = new DateOnly(2026, 9, 1),
    };

    private static RpmReading Reading(ReadingSeverity severity, decimal value, int minutesAfter = 0) => new()
    {
        EnrollmentId = Enrollment.Id,
        PatientId = Enrollment.PatientId,
        Type = RpmReadingType.SystolicBp,
        Value = value,
        Unit = "mmHg",
        MeasuredAt = Now.AddMinutes(minutesAfter),
        Severity = severity,
        IsOutOfRange = severity != ReadingSeverity.Normal,
    };

    [Fact]
    public void An_out_of_range_reading_opens_an_alert_describing_it()
    {
        var alert = RpmAlert.ForReading(Enrollment, Reading(ReadingSeverity.Warning, 152m), Now);

        alert.Status.ShouldBe(RpmAlertStatus.Open);
        alert.Severity.ShouldBe(RpmAlertSeverity.Warning);
        alert.Kind.ShouldBe(RpmAlertKind.Threshold);
        alert.Message.ShouldBe("Out of range: Systolic BP 152 mmHg");
        alert.FacilityId.ShouldBe(Enrollment.FacilityId);
        alert.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void Repeated_breaches_are_counted_without_paging_again()
    {
        var alert = RpmAlert.ForReading(Enrollment, Reading(ReadingSeverity.Warning, 152m), Now);

        alert.Recur(Reading(ReadingSeverity.Warning, 155m, minutesAfter: 60), Now.AddMinutes(61)).ShouldBeFalse();

        alert.Occurrences.ShouldBe(2);
        alert.LastOccurredAt.ShouldBe(Now.AddMinutes(60));
        alert.Severity.ShouldBe(RpmAlertSeverity.Warning);
    }

    [Fact]
    public void A_warning_that_turns_critical_escalates_and_reopens()
    {
        var alert = RpmAlert.ForReading(Enrollment, Reading(ReadingSeverity.Warning, 152m), Now);
        alert.Acknowledge(Guid.CreateVersion7(), "Called the patient", Now.AddMinutes(10));

        var escalated = alert.Recur(Reading(ReadingSeverity.Critical, 188m, minutesAfter: 120), Now.AddMinutes(121));

        escalated.ShouldBeTrue();
        alert.Severity.ShouldBe(RpmAlertSeverity.Critical);
        alert.Status.ShouldBe(RpmAlertStatus.Open);
        alert.AcknowledgedAt.ShouldBeNull();
        alert.Value.ShouldBe(188m);
        alert.Message.ShouldBe("Critical: Systolic BP 188 mmHg");
    }

    [Fact]
    public void A_critical_alert_is_not_downgraded_by_a_milder_breach()
    {
        var alert = RpmAlert.ForReading(Enrollment, Reading(ReadingSeverity.Critical, 190m), Now);

        alert.Recur(Reading(ReadingSeverity.Warning, 150m, minutesAfter: 30), Now.AddMinutes(31)).ShouldBeFalse();

        alert.Severity.ShouldBe(RpmAlertSeverity.Critical);
        alert.Value.ShouldBe(190m);
    }

    [Fact]
    public void Acknowledging_and_resolving_are_tracked()
    {
        var nurse = Guid.CreateVersion7();
        var doctor = Guid.CreateVersion7();
        var alert = RpmAlert.ForReading(Enrollment, Reading(ReadingSeverity.Critical, 190m), Now);

        alert.Acknowledge(nurse, "Advised rest and repeat reading", Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();
        alert.Acknowledge(doctor, null, Now.AddMinutes(6)).IsSuccess.ShouldBeTrue();
        alert.AcknowledgedByUserId.ShouldBe(nurse);

        alert.Resolve(doctor, "Medicine dose increased", Now.AddMinutes(30)).IsSuccess.ShouldBeTrue();
        alert.Status.ShouldBe(RpmAlertStatus.Resolved);
        alert.ResolvedByUserId.ShouldBe(doctor);
        alert.Notes.ShouldBe("Advised rest and repeat reading\nMedicine dose increased");
        alert.IsActive.ShouldBeFalse();
        alert.Acknowledge(nurse, null, Now.AddHours(1)).Error!.Code.ShouldBe("rpm.alert_resolved");
        alert.Resolve(nurse, null, Now.AddHours(1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Resolving_directly_also_records_acknowledgement()
    {
        var doctor = Guid.CreateVersion7();
        var alert = RpmAlert.ForReading(Enrollment, Reading(ReadingSeverity.Warning, 150m), Now);

        alert.Resolve(doctor, null, Now.AddMinutes(20));

        alert.AcknowledgedByUserId.ShouldBe(doctor);
        alert.AcknowledgedAt.ShouldBe(Now.AddMinutes(20));
    }

    [Fact]
    public void Silence_alerts_name_the_device()
    {
        var device = new RpmDevice { SerialNumber = "OMRON-HEM7156-00231", Type = RpmDeviceType.BpMonitor };

        var alert = RpmAlert.ForSilence(Enrollment, device, TimeSpan.FromHours(26), Now);

        alert.Kind.ShouldBe(RpmAlertKind.DeviceSilence);
        alert.Message.ShouldContain("OMRON-HEM7156-00231");
        alert.Message.ShouldContain("26 hours");
    }
}

public sealed class RpmEnrollmentTests
{
    private static readonly DateOnly Today = new(2026, 9, 28);

    [Fact]
    public void Paused_enrolments_keep_readings_but_raise_no_alerts()
    {
        var enrollment = new RpmEnrollment { Status = RpmEnrollmentStatus.Active };

        enrollment.ChangeStatus(RpmEnrollmentStatus.Paused, "Admitted to hospital", Today).IsSuccess.ShouldBeTrue();

        enrollment.RaisesAlerts.ShouldBeFalse();
        enrollment.IsClosed.ShouldBeFalse();
        enrollment.ChangeStatus(RpmEnrollmentStatus.Active, null, Today).IsSuccess.ShouldBeTrue();
        enrollment.RaisesAlerts.ShouldBeTrue();
    }

    [Fact]
    public void Withdrawing_needs_a_reason_and_closes_the_enrolment()
    {
        var enrollment = new RpmEnrollment { Status = RpmEnrollmentStatus.Active };

        enrollment.ChangeStatus(RpmEnrollmentStatus.Withdrawn, " ", Today).Error!.Type.ShouldBe(ErrorType.Validation);
        enrollment.ChangeStatus(RpmEnrollmentStatus.Withdrawn, "Moved to another city", Today).IsSuccess.ShouldBeTrue();

        enrollment.IsClosed.ShouldBeTrue();
        enrollment.EndDate.ShouldBe(Today);
        enrollment.ChangeStatus(RpmEnrollmentStatus.Active, null, Today).Error!.Code.ShouldBe("rpm.enrollment_closed");
    }

    [Fact]
    public void The_latest_reading_time_only_moves_forward()
    {
        var enrollment = new RpmEnrollment();
        var at = new DateTimeOffset(2026, 9, 28, 2, 0, 0, TimeSpan.Zero);

        enrollment.RecordReading(at);
        enrollment.RecordReading(at.AddHours(-5));

        enrollment.LastReadingAt.ShouldBe(at);
    }
}
