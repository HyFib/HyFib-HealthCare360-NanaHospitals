using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Dialysis;

/// <summary>Dialyzer reuse tracking (DIA-04).</summary>
public sealed class DialyzerTests
{
    private static readonly Guid Patient = Guid.CreateVersion7();

    [Fact]
    public void Each_use_returns_the_use_number_for_the_chart()
    {
        var dialyzer = new Dialyzer { PatientId = Patient, SerialNumber = "FX8-001", MaxUses = 6 };

        dialyzer.Use(Patient, TestData.Now).Value.ShouldBe(1);
        dialyzer.Use(Patient, TestData.Now.AddDays(2)).Value.ShouldBe(2);

        dialyzer.RemainingUses.ShouldBe(4);
        dialyzer.FirstUsedAt.ShouldBe(TestData.Now);
        dialyzer.LastUsedAt.ShouldBe(TestData.Now.AddDays(2));
    }

    [Fact]
    public void Another_patients_dialyzer_is_never_used()
    {
        var dialyzer = new Dialyzer { PatientId = Patient, SerialNumber = "FX8-001", MaxUses = 6 };

        dialyzer.Use(Guid.CreateVersion7(), TestData.Now).Error!.Code.ShouldBe("dialyzer.other_patient");
        dialyzer.UsesCount.ShouldBe(0);
    }

    [Fact]
    public void Dialyzer_is_discarded_on_reaching_its_maximum_uses()
    {
        var dialyzer = new Dialyzer { PatientId = Patient, SerialNumber = "FX8-002", MaxUses = 2 };
        dialyzer.Use(Patient, TestData.Now).IsSuccess.ShouldBeTrue();
        dialyzer.Use(Patient, TestData.Now.AddDays(2)).IsSuccess.ShouldBeTrue();

        dialyzer.IsDiscarded.ShouldBeTrue();
        dialyzer.DiscardReason!.ShouldContain("maximum");
        dialyzer.Use(Patient, TestData.Now.AddDays(4)).Error!.Code.ShouldBe("dialyzer.discarded");
    }

    [Fact]
    public void Single_use_dialyzer_cannot_be_reused()
    {
        var dialyzer = new Dialyzer { PatientId = Patient, SerialNumber = "SU-1", MaxUses = 1 };
        dialyzer.Use(Patient, TestData.Now).IsSuccess.ShouldBeTrue();

        dialyzer.EnsureUsableFor(Patient).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Discarded_dialyzer_keeps_its_first_discard_reason()
    {
        var dialyzer = new Dialyzer { PatientId = Patient, SerialNumber = "FX8-003", MaxUses = 6 };
        dialyzer.Discard("Fibre bundle volume below 80 %", TestData.Now);
        dialyzer.Discard("Second reason", TestData.Now.AddDays(1));

        dialyzer.DiscardReason.ShouldBe("Fibre bundle volume below 80 %");
        dialyzer.RemainingUses.ShouldBe(0);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, Weekday.Monday)]
    [InlineData(DayOfWeek.Saturday, Weekday.Saturday)]
    [InlineData(DayOfWeek.Sunday, Weekday.Sunday)]
    public void Weekly_schedule_maps_calendar_days(DayOfWeek day, Weekday expected) =>
        DialysisPatientProfile.ToWeekday(day).ShouldBe(expected);

    [Fact]
    public void Patient_is_due_on_the_days_of_the_weekly_schedule()
    {
        var profile = new DialysisPatientProfile { ScheduleDays = [Weekday.Monday, Weekday.Wednesday, Weekday.Friday] };
        var friday = new DateOnly(2026, 9, 25);

        profile.IsDueOn(friday).ShouldBeTrue();
        profile.IsDueOn(friday.AddDays(1)).ShouldBeFalse();

        profile.IsActive = false;
        profile.IsDueOn(friday).ShouldBeFalse();
    }
}
