using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Dialysis;

/// <summary>Sero-segregation (DIA-02): a patient may only be dialysed on a machine of exactly their sero category.</summary>
public sealed class SeroSegregationTests
{
    public static TheoryData<SeroStatus, SeroStatus> AllPairs()
    {
        var data = new TheoryData<SeroStatus, SeroStatus>();
        foreach (var patient in Enum.GetValues<SeroStatus>())
        {
            foreach (var machine in Enum.GetValues<SeroStatus>())
            {
                data.Add(patient, machine);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Only_the_same_sero_category_is_compatible(SeroStatus patient, SeroStatus machine)
    {
        SeroSegregation.IsCompatible(patient, machine).ShouldBe(patient == machine);
        SeroSegregation.EnsureCompatible(patient, machine, "HD-01").IsSuccess.ShouldBe(patient == machine);
    }

    [Fact]
    public void Mismatch_error_explains_both_categories()
    {
        var error = SeroSegregation.EnsureCompatible(SeroStatus.HBsAg, SeroStatus.Negative, "HD-02").Error!;

        error.Code.ShouldBe("dialysis.sero_mismatch");
        error.Message.ShouldContain("HD-02");
        error.Message.ShouldContain("HBsAg-positive");
        error.Message.ShouldContain("sero-negative");
    }

    [Fact]
    public void Hepatitis_b_positive_patients_never_reuse_dialyzers()
    {
        SeroSegregation.AllowsDialyzerReuse(SeroStatus.HBsAg).ShouldBeFalse();
        SeroSegregation.AllowsDialyzerReuse(SeroStatus.Negative).ShouldBeTrue();

        var profile = new DialysisPatientProfile { SeroStatus = SeroStatus.HBsAg, ReusePolicy = DialyzerReusePolicy.Reuse, MaxDialyzerUses = 8 };
        profile.EffectiveMaxDialyzerUses.ShouldBe(1);
    }

    [Fact]
    public void Reuse_policy_limits_uses_for_other_patients()
    {
        new DialysisPatientProfile { SeroStatus = SeroStatus.HCV, ReusePolicy = DialyzerReusePolicy.Reuse, MaxDialyzerUses = 8 }.EffectiveMaxDialyzerUses.ShouldBe(8);
        new DialysisPatientProfile { SeroStatus = SeroStatus.Negative, ReusePolicy = DialyzerReusePolicy.SingleUse, MaxDialyzerUses = 8 }.EffectiveMaxDialyzerUses.ShouldBe(1);
    }

    [Fact]
    public void Sero_rescreening_is_due_after_three_months_or_when_never_tested()
    {
        SeroSegregation.IsRescreeningDue(null, TestData.Today).ShouldBeTrue();
        SeroSegregation.IsRescreeningDue(TestData.Today.AddDays(-90), TestData.Today).ShouldBeFalse();
        SeroSegregation.IsRescreeningDue(TestData.Today.AddDays(-91), TestData.Today).ShouldBeTrue();
    }

    [Fact]
    public void Machine_is_ready_only_when_matched_idle_in_service_and_disinfected()
    {
        var machine = new DialysisMachine { Code = "HD-04", SeroDedication = SeroStatus.HBsAg, Status = DialysisMachineStatus.Available };

        machine.EnsureReadyFor(SeroStatus.Negative).Error!.Code.ShouldBe("dialysis.sero_mismatch");
        machine.EnsureReadyFor(SeroStatus.HBsAg).IsSuccess.ShouldBeTrue();

        machine.Status = DialysisMachineStatus.Maintenance;
        machine.EnsureReadyFor(SeroStatus.HBsAg).Error!.Code.ShouldBe("dialysis.machine_unavailable");

        machine.Status = DialysisMachineStatus.InUse;
        machine.EnsureReadyFor(SeroStatus.HBsAg).Error!.Code.ShouldBe("dialysis.machine_busy");
    }

    [Fact]
    public void Machine_must_be_disinfected_between_patients()
    {
        var machine = new DialysisMachine { Code = "HD-01", SeroDedication = SeroStatus.Negative, Status = DialysisMachineStatus.InUse };
        machine.Release(TestData.Now);

        machine.NeedsDisinfection.ShouldBeTrue();
        machine.EnsureReadyFor(SeroStatus.Negative).Error!.Code.ShouldBe("dialysis.machine_not_disinfected");

        machine.RecordDisinfection("Heat + citric acid", TestData.Now.AddMinutes(20)).IsSuccess.ShouldBeTrue();
        machine.NeedsDisinfection.ShouldBeFalse();
        machine.EnsureReadyFor(SeroStatus.Negative).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Running_machine_cannot_be_disinfected() =>
        new DialysisMachine { Code = "HD-01", Status = DialysisMachineStatus.InUse }.RecordDisinfection("Heat", TestData.Now).Error!.Code
            .ShouldBe("dialysis.machine_busy");
}
