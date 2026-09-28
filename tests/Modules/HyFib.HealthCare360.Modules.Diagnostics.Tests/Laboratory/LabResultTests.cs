using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>Result life cycle: Pending → Entered → Verified (manual or automatic), reopen before release, amend after.</summary>
public sealed class LabResultTests
{
    [Fact]
    public void Entry_stores_the_interpreted_value_as_it_was_at_entry()
    {
        var result = Result();
        var delta = new DeltaOutcome(4.1m, Now.AddDays(-3), 31.7m, true);

        result.Record(Capture("5.4", 5.4m, ResultFlag.High) with { Delta = delta, Remarks = "Repeated, same value." }, UserId, Now)
            .IsSuccess.ShouldBeTrue();

        result.Status.ShouldBe(LabResultStatus.Entered);
        result.ValueText.ShouldBe("5.4");
        result.NumericValue.ShouldBe(5.4m);
        result.Flag.ShouldBe(ResultFlag.High);
        result.ReferenceText.ShouldBe("3.5 - 5.1");
        result.PreviousValue.ShouldBe(4.1m);
        result.DeltaPercent.ShouldBe(31.7m);
        result.DeltaExceeded.ShouldBeTrue();
        result.Method.ShouldBe("ISE");
        result.Remarks.ShouldBe("Repeated, same value.");
        result.EnteredBy.ShouldBe(UserId);
        result.EnteredAt.ShouldBe(Now);
    }

    [Fact]
    public void A_value_can_be_corrected_until_it_is_verified()
    {
        var result = Result();
        result.Record(Capture("5.4", 5.4m, ResultFlag.High), UserId, Now);

        result.Record(Capture("4.5", 4.5m) with { Method = null }, UserId, Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();

        result.ValueText.ShouldBe("4.5");
        result.Flag.ShouldBe(ResultFlag.Normal);
        result.Method.ShouldBe("ISE");
        result.EnteredAt.ShouldBe(Now.AddMinutes(5));
    }

    [Fact]
    public void A_verified_result_cannot_be_overwritten_by_entry_or_by_an_analyser()
    {
        var result = Result(LabResultStatus.Verified, ResultFlag.Normal);
        result.ValueText = "4.2";

        result.Record(Capture("9.9", 9.9m, ResultFlag.CriticalHigh), UserId, Now).Error!.Code.ShouldBe("lab.result_verified");

        result.ValueText.ShouldBe("4.2");
    }

    [Fact]
    public void A_missing_value_cannot_be_verified() =>
        Result().Verify(UserId, Now, automatic: false).Error!.Code.ShouldBe("lab.result_missing");

    [Fact]
    public void Manual_verification_records_the_pathologist()
    {
        var result = Result(LabResultStatus.Entered, ResultFlag.Normal);

        result.Verify(UserId, Now, automatic: false).IsSuccess.ShouldBeTrue();

        result.Status.ShouldBe(LabResultStatus.Verified);
        result.VerifiedBy.ShouldBe(UserId);
        result.VerifiedAt.ShouldBe(Now);
        result.AutoValidated.ShouldBeFalse();
    }

    [Fact]
    public void Auto_validation_is_marked_as_such_and_names_no_person()
    {
        var result = Result(LabResultStatus.Entered, ResultFlag.Normal);

        result.Verify(UserId, Now, automatic: true).IsSuccess.ShouldBeTrue();

        result.AutoValidated.ShouldBeTrue();
        result.VerifiedBy.ShouldBeNull();
    }

    [Fact]
    public void Verifying_twice_keeps_the_first_sign_off()
    {
        var result = Result(LabResultStatus.Entered, ResultFlag.Normal);
        result.Verify(UserId, Now, automatic: false);

        result.Verify(Guid.CreateVersion7(), Now.AddHours(1), automatic: false).IsSuccess.ShouldBeTrue();

        result.VerifiedBy.ShouldBe(UserId);
        result.VerifiedAt.ShouldBe(Now);
    }

    [Fact]
    public void Reopening_sends_a_verified_result_back_for_correction()
    {
        var result = Result(LabResultStatus.Entered, ResultFlag.Normal);
        result.Verify(UserId, Now, automatic: true);

        result.Reopen().IsSuccess.ShouldBeTrue();

        result.Status.ShouldBe(LabResultStatus.Entered);
        result.VerifiedAt.ShouldBeNull();
        result.VerifiedBy.ShouldBeNull();
        result.AutoValidated.ShouldBeFalse();
        result.Reopen().Error!.Code.ShouldBe("lab.result_not_verified");
    }

    [Fact]
    public void Amendment_replaces_the_value_and_makes_the_amending_pathologist_the_verifier()
    {
        var result = Result(LabResultStatus.Entered, ResultFlag.Normal);
        result.Record(Capture("4.2", 4.2m), UserId, Now);
        result.Verify(null, Now, automatic: true);
        var pathologist = Guid.CreateVersion7();

        result.Amend(Capture("6.8", 6.8m, ResultFlag.CriticalHigh), pathologist, Now.AddHours(2)).IsSuccess.ShouldBeTrue();

        result.ValueText.ShouldBe("6.8");
        result.IsCritical.ShouldBeTrue();
        result.Status.ShouldBe(LabResultStatus.Verified);
        result.VerifiedBy.ShouldBe(pathologist);
        result.VerifiedAt.ShouldBe(Now.AddHours(2));
        result.AutoValidated.ShouldBeFalse();
    }

    [Fact]
    public void Only_verified_results_are_amended() =>
        Result(LabResultStatus.Entered).Amend(Capture("4.0", 4.0m), UserId, Now).Error!.Code.ShouldBe("lab.result_not_verified");

    [Theory]
    [InlineData(ResultFlag.CriticalLow, true)]
    [InlineData(ResultFlag.CriticalHigh, true)]
    [InlineData(ResultFlag.Critical, true)]
    [InlineData(ResultFlag.High, false)]
    [InlineData(ResultFlag.Abnormal, false)]
    [InlineData(ResultFlag.Normal, false)]
    public void Critical_flags_are_recognised(ResultFlag flag, bool critical) =>
        Result(LabResultStatus.Entered, flag).IsCritical.ShouldBe(critical);

    [Fact]
    public void A_result_without_a_flag_is_not_critical() => Result().IsCritical.ShouldBeFalse();

    [Fact]
    public void Description_prints_the_value_with_its_unit()
    {
        var result = Result();
        result.Describe().ShouldBe("(no value)");

        result.Record(Capture("6.8", 6.8m, ResultFlag.CriticalHigh), UserId, Now);
        result.Describe().ShouldBe("6.8 mmol/L");

        result.Unit = null;
        result.Describe().ShouldBe("6.8");
    }
}
