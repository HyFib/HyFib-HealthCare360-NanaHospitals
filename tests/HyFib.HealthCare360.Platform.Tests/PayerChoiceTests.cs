using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The rule for putting a visit, an admission or a stay on a third party's account (<see cref="PayerChoice"/>): the payer
/// named, in use and of the kind chosen, and standing behind the patient through a policy verified by a second person.
/// </summary>
public sealed class PayerChoiceTests
{
    private static readonly Guid CompanyId = Guid.CreateVersion7();

    private static PayerStanding Standing(PayerCover cover, PayerType kind = PayerType.Corporate, bool inUse = true) =>
        new(new PayerSummary(CompanyId, "Kaveri Textiles", kind, inUse), cover, cover == PayerCover.Verified ? new DateOnly(2027, 3, 31) : null);

    [Theory]
    [InlineData(PayerType.Self)]
    [InlineData(PayerType.Staff)]
    [InlineData(PayerType.Charity)]
    public void The_patient_staff_and_charity_are_chosen_freely(PayerType type)
    {
        PayerChoice.IsThirdParty(type).ShouldBeFalse();
        PayerChoice.Refusal(type, null, null).ShouldBeNull();
        PayerChoice.Refusal(type, CompanyId, Standing(PayerCover.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(PayerType.Insurance, "Choose the insurer that pays.")]
    [InlineData(PayerType.Corporate, "Choose the company that pays.")]
    [InlineData(PayerType.GovernmentScheme, "Choose the government scheme that pays.")]
    public void A_third_party_is_named(PayerType type, string message)
    {
        var refused = PayerChoice.Refusal(type, null, null).ShouldNotBeNull();
        (refused.Type, refused.Message).ShouldBe((ErrorType.Validation, message));
    }

    [Fact]
    public void A_third_party_is_of_the_payer_master_in_use_and_of_the_kind_chosen()
    {
        PayerChoice.Refusal(PayerType.Corporate, CompanyId, null).ShouldNotBeNull().Message.ShouldBe("Choose a payer of the payer master.");
        PayerChoice.Refusal(PayerType.Corporate, CompanyId, Standing(PayerCover.Verified, inUse: false)).ShouldNotBeNull()
            .Message.ShouldBe("Kaveri Textiles is out of use.");
        var miscast = PayerChoice.Refusal(PayerType.Corporate, CompanyId, Standing(PayerCover.Verified, kind: PayerType.Insurance)).ShouldNotBeNull();
        miscast.Message.ShouldBe("Kaveri Textiles pays as an insurer, not as a company.");
        miscast.Fields.ShouldNotBeNull().Keys.ShouldBe(["payerType"]);
    }

    [Fact]
    public void A_third_party_pays_only_on_the_patients_cover_verified_by_a_second_person()
    {
        PayerChoice.Refusal(PayerType.Corporate, CompanyId, Standing(PayerCover.None)).ShouldNotBeNull().ShouldSatisfyAllConditions(
            e => e.Code.ShouldBe("payer.no_cover"),
            e => e.Type.ShouldBe(ErrorType.BusinessRule),
            e => e.Message.ShouldContain("have someone else verify it"));
        PayerChoice.Refusal(PayerType.Corporate, CompanyId, Standing(PayerCover.NotVerified)).ShouldNotBeNull().ShouldSatisfyAllConditions(
            e => e.Code.ShouldBe("payer.cover_not_verified"),
            e => e.Message.ShouldContain("Someone other than whoever recorded it verifies it"));
        PayerChoice.Refusal(PayerType.Corporate, CompanyId, Standing(PayerCover.Verified)).ShouldBeNull();
    }
}
