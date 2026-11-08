using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>A doctor's consultation fees, in the words the approver reads (maker-checker, slice 6).</summary>
public sealed class PractitionerFeesTests
{
    [Fact]
    public void Each_fee_that_changes_is_named_with_before_and_after()
    {
        PractitionerFeeApprovals.Describe(new PractitionerFees(500m, 300m), new PractitionerFees(700m, 300m)).ShouldBe("new visit ₹500.00 → ₹700.00");
        PractitionerFeeApprovals.Describe(new PractitionerFees(500m, 300m), new PractitionerFees(400m, 200m))
            .ShouldBe("new visit ₹500.00 → ₹400.00; follow-up ₹300.00 → ₹200.00");
    }

    [Fact]
    public void No_fee_of_their_own_is_the_service_price()
    {
        PractitionerFeeApprovals.Describe(PractitionerFees.None, new PractitionerFees(777m, 0m)).ShouldBe("new visit none (the service price) → ₹777.00");
        PractitionerFeeApprovals.Describe(new PractitionerFees(777m, 0m), new PractitionerFees(777m, 0m)).ShouldBe("unchanged");
    }

    [Fact]
    public void Fees_are_read_from_and_set_on_the_practitioner()
    {
        var doctor = new Practitioner { ConsultationFeeNew = 600m, ConsultationFeeFollowUp = 350m };

        PractitionerFees.Of(doctor).ShouldBe(new PractitionerFees(600m, 350m));
        new PractitionerFees(800m, 0m).ApplyTo(doctor);
        (doctor.ConsultationFeeNew, doctor.ConsultationFeeFollowUp).ShouldBe((800m, 0m));
    }
}
