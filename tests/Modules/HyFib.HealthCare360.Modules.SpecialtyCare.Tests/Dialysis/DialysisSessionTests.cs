using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Dialysis;

public sealed class DialysisSessionTests
{
    private static readonly Guid Patient = Guid.CreateVersion7();

    private static DialysisSession Scheduled() => new() { PatientId = Patient, SessionNumber = "NHDL26000001", Status = DialysisSessionStatus.Scheduled };

    private static DialysisMachine Machine(SeroStatus sero = SeroStatus.Negative) =>
        new() { Code = "HD-01", SeroDedication = sero, Status = DialysisMachineStatus.Available };

    private static Dialyzer OwnDialyzer() => new() { PatientId = Patient, SerialNumber = "FX8-1", MaxUses = 8 };

    private static DialysisSession Running(DialysisMachine machine)
    {
        var session = Scheduled();
        session.RecordPreAssessment(new PreDialysisAssessment(64.2m, 150, 90, 82, 36.8m, 2500), TestData.Now).IsSuccess.ShouldBeTrue();
        session.Start(machine, OwnDialyzer(), SeroStatus.Negative, 2000, 500, TestData.Staff()).IsSuccess.ShouldBeTrue();
        return session;
    }

    [Fact]
    public void Session_cannot_start_without_the_pre_dialysis_assessment() =>
        Scheduled().Start(Machine(), OwnDialyzer(), SeroStatus.Negative, null, null, TestData.Staff()).Error!.Code
            .ShouldBe("dialysis.pre_assessment_missing");

    [Fact]
    public void Positive_patient_cannot_be_connected_to_a_negative_machine()
    {
        var session = Scheduled();
        session.RecordPreAssessment(new PreDialysisAssessment(60m, 140, 80, 80, null, null), TestData.Now).IsSuccess.ShouldBeTrue();
        var dialyzer = OwnDialyzer();

        var result = session.Start(Machine(SeroStatus.Negative), dialyzer, SeroStatus.HCV, null, null, TestData.Staff());

        result.Error!.Code.ShouldBe("dialysis.sero_mismatch");
        session.Status.ShouldBe(DialysisSessionStatus.Scheduled);
        dialyzer.UsesCount.ShouldBe(0, "nothing is consumed when the safety check fails");
    }

    [Fact]
    public void Machine_assignment_is_sero_checked_at_scheduling() =>
        Scheduled().AssignMachine(Machine(SeroStatus.HIV), SeroStatus.Negative).Error!.Code.ShouldBe("dialysis.sero_mismatch");

    [Fact]
    public void Starting_records_machine_dialyzer_use_number_and_performer()
    {
        var machine = Machine();

        var session = Running(machine);

        session.Status.ShouldBe(DialysisSessionStatus.InProgress);
        session.DialyzerUseNumber.ShouldBe(1);
        session.MachineId.ShouldBe(machine.Id);
        session.PerformedByName.ShouldBe("Sr. Anita Joseph");
        machine.Status.ShouldBe(DialysisMachineStatus.InUse);
    }

    [Fact]
    public void Another_patients_dialyzer_blocks_the_start()
    {
        var session = Scheduled();
        session.RecordPreAssessment(new PreDialysisAssessment(60m, 140, 80, 80, null, null), TestData.Now).IsSuccess.ShouldBeTrue();
        var foreign = new Dialyzer { PatientId = Guid.CreateVersion7(), SerialNumber = "X", MaxUses = 8 };

        session.Start(Machine(), foreign, SeroStatus.Negative, null, null, TestData.Staff()).Error!.Code.ShouldBe("dialyzer.other_patient");
    }

    [Fact]
    public void Completion_derives_ultrafiltration_from_weight_loss_and_frees_the_machine_for_disinfection()
    {
        var machine = Machine();
        var session = Running(machine);
        session.AddReadings([new DialysisReading(TestData.Now.AddHours(1), 130, 80, 84, 300, 600, 150, -120, 80, null)]).IsSuccess.ShouldBeTrue();

        session.Complete(new PostDialysisAssessment(61.7m, 128, 78, 80, null), [DialysisComplication.MuscleCramps], null, machine, TestData.Now.AddHours(4))
            .IsSuccess.ShouldBeTrue();

        session.Status.ShouldBe(DialysisSessionStatus.Completed);
        session.UfAchievedMl.ShouldBe(2500);
        session.DurationMinutes.ShouldBe(240);
        session.Readings.ShouldHaveSingleItem();
        machine.Status.ShouldBe(DialysisMachineStatus.Available);
        machine.NeedsDisinfection.ShouldBeTrue();
    }

    [Fact]
    public void Complication_other_needs_a_description()
    {
        var machine = Machine();
        var session = Running(machine);

        session.Complete(new PostDialysisAssessment(62m, 120, 80, null, 2000), [DialysisComplication.Other], " ", machine, TestData.Now.AddHours(4))
            .Error!.Fields!.ShouldContainKey("complicationNotes");
    }

    [Fact]
    public void Readings_are_only_taken_while_running() =>
        Scheduled().AddReadings([new DialysisReading(TestData.Now, 120, 80, 80, 300, 500, null, null, null, null)]).IsFailure.ShouldBeTrue();

    [Fact]
    public void Missed_and_cancelled_sessions_are_distinguished()
    {
        var missed = Scheduled();
        missed.Cancel("Did not come", patientDidNotCome: true).IsSuccess.ShouldBeTrue();
        missed.Status.ShouldBe(DialysisSessionStatus.Missed);

        var cancelled = Scheduled();
        cancelled.Cancel("Admitted to ICU", patientDidNotCome: false).IsSuccess.ShouldBeTrue();
        cancelled.Status.ShouldBe(DialysisSessionStatus.Cancelled);
    }

    [Fact]
    public void Running_session_cannot_be_cancelled() =>
        Running(Machine()).Cancel("Too late", false).IsFailure.ShouldBeTrue();
}
