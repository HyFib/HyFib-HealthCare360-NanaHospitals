using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>Who the MLC register says is treated free of cost by law (BNSS 2023, s.397), and what their bills say of it.</summary>
public sealed class FreeTreatmentTests
{
    [Fact]
    public void The_victim_of_a_sexual_assault_or_an_acid_attack_is_treated_free()
    {
        Enum.GetValues<MlcCause>().Where(FreeTreatment.Covers).ShouldBe([MlcCause.SexualAssault, MlcCause.AcidAttack], ignoreOrder: true);
    }

    [Fact]
    public void Its_bills_name_the_law_and_the_mlc_entry_and_never_the_offence()
    {
        var reason = FreeTreatment.Reason("NHMLC2600012");

        reason.ShouldBe("BNSS 2023 s.397, MLC no. NHMLC2600012");
        foreach (var word in new[] { "sexual", "rape", "assault", "acid", "victim", "POCSO" })
        {
            reason.ShouldNotContain(word, Case.Insensitive);
        }
    }
}
