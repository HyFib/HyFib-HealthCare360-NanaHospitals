using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests;

/// <summary>The critical call register: a critical value is only signed off after a documented, read-back call.</summary>
public sealed class CriticalCallLogTests
{
    private static readonly CriticalFindingRef Potassium = new(
        DiagnosticKind.Laboratory, Guid.CreateVersion7(), Guid.CreateVersion7(), "Potassium 6.9 mmol/L (critical high)", LabResultId: Guid.CreateVersion7());

    private static readonly CriticalCallDetails Call = new(
        " Dr. Meena Iyer ", " Duty doctor, MICU ", Guid.CreateVersion7(), CommunicationMethod.Phone, Now.AddMinutes(-3), true, " Repeat sample advised ");

    [Fact]
    public void A_documented_call_records_the_finding_the_person_and_the_read_back()
    {
        var log = CriticalCallLog.Record(Potassium, Call, UserId, Now).Value;

        log.Kind.ShouldBe(DiagnosticKind.Laboratory);
        log.FacilityId.ShouldBe(Potassium.FacilityId);
        log.PatientId.ShouldBe(Potassium.PatientId);
        log.LabResultId.ShouldBe(Potassium.LabResultId);
        log.Finding.ShouldBe("Potassium 6.9 mmol/L (critical high)");
        log.InformedPersonName.ShouldBe("Dr. Meena Iyer");
        log.InformedPersonRole.ShouldBe("Duty doctor, MICU");
        log.InformedPractitionerId.ShouldBe(Call.InformedPractitionerId);
        log.Method.ShouldBe(CommunicationMethod.Phone);
        log.InformedAt.ShouldBe(Now.AddMinutes(-3));
        log.ReadBackConfirmed.ShouldBeTrue();
        log.CalledByUserId.ShouldBe(UserId);
        log.Notes.ShouldBe("Repeat sample advised");
    }

    [Fact]
    public void The_person_informed_must_be_named() =>
        CriticalCallLog.Record(Potassium, Call with { InformedPersonName = " " }, UserId, Now).Error!.Fields!.Keys
            .ShouldContain("criticalCall.informedPersonName");

    [Fact]
    public void A_call_without_read_back_is_refused()
    {
        var result = CriticalCallLog.Record(Potassium, Call with { ReadBackConfirmed = false }, UserId, Now);

        result.Error!.Code.ShouldBe("critical.read_back_required");
        result.Error.Type.ShouldBe(ErrorType.BusinessRule);
    }

    [Fact]
    public void The_call_time_may_differ_from_the_server_clock_by_a_few_minutes_but_not_lie_in_the_future()
    {
        CriticalCallLog.Record(Potassium, Call with { InformedAt = Now.AddMinutes(4) }, UserId, Now).IsSuccess.ShouldBeTrue();
        CriticalCallLog.Record(Potassium, Call with { InformedAt = Now.AddMinutes(6) }, UserId, Now).Error!.Fields!.Keys
            .ShouldContain("criticalCall.informedAt");
    }

    [Fact]
    public void Imaging_findings_are_logged_against_the_study_and_long_descriptions_are_clipped()
    {
        var study = Guid.CreateVersion7();
        var finding = new CriticalFindingRef(DiagnosticKind.Radiology, Guid.CreateVersion7(), Guid.CreateVersion7(), new string('x', 450), ImagingStudyId: study);

        var log = CriticalCallLog.Record(finding, Call with { Method = CommunicationMethod.InPerson, InformedPersonRole = null, Notes = null }, UserId, Now).Value;

        log.ImagingStudyId.ShouldBe(study);
        log.LabResultId.ShouldBeNull();
        log.Finding.Length.ShouldBe(300);
        log.InformedPersonRole.ShouldBeNull();
        log.Notes.ShouldBeNull();
    }
}
