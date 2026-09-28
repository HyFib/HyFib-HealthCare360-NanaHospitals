namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

public sealed class SubstitutionPolicyTests
{
    private static readonly Guid Prescribed = Guid.CreateVersion7();
    private static readonly Guid Other = Guid.CreateVersion7();

    private static Result<bool> Evaluate(
        Guid? prescribedItem, string? prescribedGeneric, string? prescribedStrength, Guid dispensedItem, string? dispensedGeneric, string? dispensedStrength,
        string? reason = null, bool consent = false) =>
        SubstitutionPolicy.Evaluate(new SubstitutionCheck(prescribedItem, prescribedGeneric, prescribedStrength, dispensedItem, dispensedGeneric, dispensedStrength, reason, consent));

    [Fact]
    public void Dispensing_the_prescribed_item_is_not_a_substitution()
    {
        Evaluate(Prescribed, "Paracetamol", "500 mg", Prescribed, "Paracetamol", "500 mg").Value.ShouldBeFalse();
    }

    [Fact]
    public void Any_brand_of_a_generically_prescribed_drug_is_not_a_substitution()
    {
        Evaluate(null, "Paracetamol", "500 mg", Other, "PARACETAMOL", "500mg").Value.ShouldBeFalse();
    }

    [Fact]
    public void A_generic_equivalent_needs_a_reason_and_the_prescribers_consent()
    {
        Evaluate(Prescribed, "Pantoprazole", "40 mg", Other, "Pantoprazole", "40 mg").Error!.Code.ShouldBe("validation");
        Evaluate(Prescribed, "Pantoprazole", "40 mg", Other, "Pantoprazole", "40 mg", "Prescribed brand out of stock").Error!.Code
            .ShouldBe("pharmacy.substitution_consent");
        Evaluate(Prescribed, "Pantoprazole", "40 mg", Other, "Pantoprazole", "40 mg", "Prescribed brand out of stock", consent: true).Value
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("Pantoprazole", "40 mg", "Rabeprazole", "20 mg")]
    [InlineData("Paracetamol", "500 mg", "Paracetamol", "650 mg")]
    public void Non_equivalents_are_never_substituted(string prescribedGeneric, string prescribedStrength, string dispensedGeneric, string dispensedStrength)
    {
        Evaluate(Prescribed, prescribedGeneric, prescribedStrength, Other, dispensedGeneric, dispensedStrength, "patient preference", consent: true)
            .Error!.Code.ShouldBe("pharmacy.substitution_not_equivalent");
    }

    [Fact]
    public void An_unverifiable_free_text_drug_is_treated_as_a_substitution()
    {
        Evaluate(null, null, null, Other, "Ondansetron", "4 mg").Error!.Code.ShouldBe("validation");
        Evaluate(null, null, null, Other, "Ondansetron", "4 mg", "Mapped from handwritten name", consent: true).Value.ShouldBeTrue();
    }

    [Fact]
    public void Names_and_strengths_are_compared_ignoring_case_spaces_and_punctuation()
    {
        SubstitutionPolicy.Normalize("500 mg").ShouldBe(SubstitutionPolicy.Normalize("500MG"));
        SubstitutionPolicy.Normalize("Amoxicillin + Clavulanic acid").ShouldBe(SubstitutionPolicy.Normalize("amoxicillin clavulanic-acid"));
    }
}
