using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.BloodBank;

/// <summary>Unit status machine: quarantine release on TTI (BB-04), expiry (BB-05), issue and cold-chain return (BB-07).</summary>
public sealed class BloodComponentTests
{
    private static readonly Guid Request = Guid.CreateVersion7();

    private static readonly TtiPanel Clear = new()
    {
        Hiv = TtiResult.NonReactive, HbsAg = TtiResult.NonReactive, Hcv = TtiResult.NonReactive, Syphilis = TtiResult.NonReactive, Malaria = TtiResult.NonReactive,
    };

    private static BloodComponent Unit(BloodComponentType type = BloodComponentType.PRBC) =>
        BloodComponent.Prepare(new Donation { BagNumber = "NHBB26000001", VolumeMl = 450, CollectedAt = TestData.Now }, type, null, null, TestData.Now);

    private static BloodComponent Available(BloodComponentType type = BloodComponentType.PRBC)
    {
        var unit = Unit(type);
        unit.ApplyLabResults(BloodGroup.OPositive, Clear, TestData.Now);
        unit.Status.ShouldBe(BloodUnitStatus.Available);
        return unit;
    }

    [Fact]
    public void Unit_leaves_quarantine_only_when_every_tti_marker_is_non_reactive()
    {
        var unit = Unit();

        unit.ApplyLabResults(BloodGroup.OPositive, Clear with { Malaria = TtiResult.Pending }, TestData.Now);
        unit.Status.ShouldBe(BloodUnitStatus.Quarantine);

        unit.ApplyLabResults(BloodGroup.OPositive, Clear, TestData.Now);
        unit.Status.ShouldBe(BloodUnitStatus.Available);
    }

    [Theory]
    [InlineData("Hiv")]
    [InlineData("HbsAg")]
    [InlineData("Hcv")]
    [InlineData("Syphilis")]
    [InlineData("Malaria")]
    public void Any_reactive_marker_discards_the_unit(string marker)
    {
        var tti = marker switch
        {
            "Hiv" => Clear with { Hiv = TtiResult.Reactive },
            "HbsAg" => Clear with { HbsAg = TtiResult.Reactive },
            "Hcv" => Clear with { Hcv = TtiResult.Reactive },
            "Syphilis" => Clear with { Syphilis = TtiResult.Reactive },
            _ => Clear with { Malaria = TtiResult.Reactive },
        };
        var unit = Unit();

        unit.ApplyLabResults(BloodGroup.OPositive, tti, TestData.Now);

        unit.Status.ShouldBe(BloodUnitStatus.Discarded);
        unit.DiscardReason!.ShouldContain("Reactive");
    }

    [Fact]
    public void Unit_without_a_blood_group_stays_in_quarantine()
    {
        var unit = Unit();
        unit.ApplyLabResults(BloodGroup.Unknown, Clear, TestData.Now);
        unit.Status.ShouldBe(BloodUnitStatus.Quarantine);
    }

    [Fact]
    public void Quarantined_unit_cannot_be_reserved_or_issued()
    {
        var unit = Unit();

        unit.Reserve(Request, TestData.Now).Error!.Code.ShouldBe("bloodbank.unit_unavailable");
        unit.Issue(Request, TestData.Now).Error!.Code.ShouldBe("bloodbank.unit_not_screened");
    }

    [Fact]
    public void Expired_unit_can_never_be_reserved_or_issued()
    {
        var unit = Available();
        var afterExpiry = unit.ExpiresAt;

        unit.Reserve(Request, afterExpiry).Error!.Code.ShouldBe("bloodbank.unit_expired");
        unit.Issue(Request, afterExpiry).Error!.Code.ShouldBe("bloodbank.unit_expired");
    }

    [Fact]
    public void Unit_reserved_for_another_request_cannot_be_issued()
    {
        var unit = Available();
        unit.Reserve(Request, TestData.Now).IsSuccess.ShouldBeTrue();

        unit.Issue(Guid.CreateVersion7(), TestData.Now).Error!.Code.ShouldBe("bloodbank.unit_unavailable");
        unit.Issue(Request, TestData.Now).IsSuccess.ShouldBeTrue();
        unit.Status.ShouldBe(BloodUnitStatus.Issued);
    }

    [Fact]
    public void Released_reservation_returns_the_unit_to_stock()
    {
        var unit = Available();
        unit.Reserve(Request, TestData.Now).IsSuccess.ShouldBeTrue();

        unit.ReleaseReservation(TestData.Now);

        unit.Status.ShouldBe(BloodUnitStatus.Available);
        unit.ReservedForRequestId.ShouldBeNull();
    }

    [Fact]
    public void Unit_returned_within_thirty_minutes_in_cold_chain_goes_back_to_stock()
    {
        var unit = Available();
        unit.Issue(Request, TestData.Now).IsSuccess.ShouldBeTrue();

        unit.Return(TestData.Now, TestData.Now.AddMinutes(30), temperatureMaintained: true).IsSuccess.ShouldBeTrue();

        unit.Status.ShouldBe(BloodUnitStatus.Available);
        unit.IssuedAt.ShouldBeNull();
    }

    [Fact]
    public void Unit_returned_after_thirty_minutes_is_discarded()
    {
        var unit = Available();
        unit.Issue(Request, TestData.Now).IsSuccess.ShouldBeTrue();

        unit.Return(TestData.Now, TestData.Now.AddMinutes(31), temperatureMaintained: true).IsSuccess.ShouldBeTrue();

        unit.Status.ShouldBe(BloodUnitStatus.Discarded);
        unit.DiscardReason!.ShouldContain("30 minutes");
    }

    [Fact]
    public void Unit_whose_cold_chain_was_broken_is_discarded()
    {
        var unit = Available();
        unit.Issue(Request, TestData.Now).IsSuccess.ShouldBeTrue();

        unit.Return(TestData.Now, TestData.Now.AddMinutes(5), temperatureMaintained: false).IsSuccess.ShouldBeTrue();

        unit.Status.ShouldBe(BloodUnitStatus.Discarded);
    }

    [Fact]
    public void Thawed_plasma_is_never_restocked()
    {
        ColdChain.CanRestock(BloodComponentType.FFP, TestData.Now, TestData.Now.AddMinutes(5), true).Error!.Code.ShouldBe("bloodbank.thawed_plasma");
        ColdChain.CanRestock(BloodComponentType.Cryoprecipitate, TestData.Now, TestData.Now.AddMinutes(5), true).IsFailure.ShouldBeTrue();
        ColdChain.CanRestock(BloodComponentType.PlateletConcentrate, TestData.Now, TestData.Now.AddMinutes(5), true).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Only_issued_units_can_be_returned_and_only_in_stock_units_discarded()
    {
        var unit = Available();
        unit.Return(TestData.Now, TestData.Now, true).Error!.Code.ShouldBe("bloodbank.unit_not_issued");

        unit.Issue(Request, TestData.Now).IsSuccess.ShouldBeTrue();
        unit.Discard("Bag leaking", TestData.Now).Error!.Code.ShouldBe("bloodbank.unit_not_in_stock");
    }

    [Fact]
    public void Expiry_job_marks_in_stock_units_and_frees_reservations()
    {
        var reserved = Available();
        reserved.Reserve(Request, TestData.Now).IsSuccess.ShouldBeTrue();
        var fresh = Available();

        reserved.MarkExpiredIfDue(reserved.ExpiresAt).ShouldBeTrue();
        fresh.MarkExpiredIfDue(fresh.ExpiresAt.AddSeconds(-1)).ShouldBeFalse();

        reserved.Status.ShouldBe(BloodUnitStatus.Expired);
        reserved.ReservedForRequestId.ShouldBeNull();
        reserved.MarkExpiredIfDue(reserved.ExpiresAt.AddDays(1)).ShouldBeFalse("already expired");
    }
}
