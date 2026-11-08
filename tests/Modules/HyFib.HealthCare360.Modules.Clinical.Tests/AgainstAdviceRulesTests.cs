using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// A patient leaving against medical advice, and the death certificate a stay is closed with (maker-checker, slice 8): what
/// the declaration must say, how it is put in words, and when a certificate is enough to close a stay as expired.
/// </summary>
public sealed class AgainstAdviceRulesTests
{
    [Fact]
    public void A_relative_who_signs_is_named_with_the_relationship_and_a_refusal_needs_no_name()
    {
        AgainstAdviceRules.Check(AgainstAdviceSignatory.Patient, null, null).ShouldBeNull();
        AgainstAdviceRules.Check(AgainstAdviceSignatory.RefusedToSign, null, null).ShouldBeNull();
        AgainstAdviceRules.Check(AgainstAdviceSignatory.Relative, "Ravi Kumar", "Son").ShouldBeNull();
        AgainstAdviceRules.Check(AgainstAdviceSignatory.Relative, "Ravi Kumar", " ")!.Code.ShouldBe("validation");
        AgainstAdviceRules.Check(AgainstAdviceSignatory.Relative, null, "Son")!.Code.ShouldBe("validation");
    }

    [Fact]
    public void The_declaration_is_put_in_words()
    {
        var record = new AgainstAdviceRecord(AgainstAdviceSignatory.Relative, "Ravi Kumar", "son", Guid.NewGuid(), "Dr Rao", Guid.NewGuid(), "Sister Mary", null);
        record.Words.ShouldBe("Signed by Ravi Kumar (son); risks explained by Dr Rao; witnessed by Sister Mary");
        AgainstAdviceRules.Signed(AgainstAdviceSignatory.Patient, null, null).ShouldBe("Signed by the patient");
        AgainstAdviceRules.Signed(AgainstAdviceSignatory.RefusedToSign, null, null).ShouldBe("Refused to sign");
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, false, true)]
    public void A_certificate_closes_a_stay_once_a_second_doctor_confirmed_it(bool required, bool confirmed, bool reported, bool settled) =>
        DeathCertificateRule.Settled(required, confirmed, reported).ShouldBe(settled);

    [Fact]
    public void The_certificate_as_the_ward_sees_it_is_put_in_words()
    {
        var at = new DateTimeOffset(2026, 11, 8, 4, 0, 0, TimeSpan.Zero);
        var unconfirmed = new DeathCertificateStanding(Guid.NewGuid(), "D-0042", null, at, false, "Dr Rao", true, null, null, null, false);
        unconfirmed.Words.ShouldBe("D-0042, not yet confirmed by a second doctor");
        (unconfirmed with { ConfirmationWaitingSince = at }).Words.ShouldBe("D-0042, waiting for a second doctor's confirmation");
        (unconfirmed with { ConfirmedBy = "Dr Iyer", ConfirmedAt = at }).Words.ShouldBe("D-0042, confirmed by Dr Iyer");
        (unconfirmed with { ConfirmationRequired = false }).Words.ShouldBe("D-0042, certified by Dr Rao");
    }
}
