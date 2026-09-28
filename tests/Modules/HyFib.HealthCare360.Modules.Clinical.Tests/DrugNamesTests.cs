using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class DrugNamesTests
{
    [Theory]
    [InlineData("Amoxycillin", "amoxicillin")]
    [InlineData("Frusemide", "furosemide")]
    [InlineData("Acetaminophen", "paracetamol")]
    [InlineData("Glyceryl Trinitrate", "nitroglycerin")]
    [InlineData("Sodium Valproate", "valproate")]
    [InlineData("Thyroxine", "levothyroxine")]
    public void Indian_and_us_spellings_normalise_to_one_name(string input, string expected) => DrugNames.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Combination_generics_split_into_ingredients() =>
        DrugNames.Components("Amoxycillin + Potassium Clavulanate").ShouldBe(["amoxicillin", "clavulanate"]);

    [Theory]
    [InlineData("Sulfamethoxazole/Trimethoprim")]
    [InlineData("Co-trimoxazole")]
    [InlineData("Sulphamethoxazole and Trimethoprim")]
    public void Co_trimoxazole_is_recognised_in_every_form(string generic) =>
        DrugNames.Components(generic).ShouldBe(["sulfamethoxazole", "trimethoprim"]);

    [Theory]
    [InlineData("Losartan Potassium", "losartan")]
    [InlineData("Warfarin Sodium", "warfarin")]
    [InlineData("Metoprolol Succinate", "metoprolol")]
    [InlineData("Ferrous Sulphate", "ferrous")]
    [InlineData("Potassium Chloride", "potassium chloride")]
    [InlineData("Calcium Carbonate", "calcium carbonate")]
    [InlineData("Magnesium Sulfate", "magnesium sulfate")]
    public void Salts_are_removed_only_when_a_real_ingredient_remains(string generic, string expected) =>
        DrugNames.Components(generic).ShouldHaveSingleItem().ShouldBe(expected);

    [Fact]
    public void Product_name_is_used_when_the_generic_is_missing() =>
        DrugNames.Components(null, "Crocin 500").ShouldHaveSingleItem().ShouldBe("crocin 500");

    [Fact]
    public void Terms_match_as_whole_words_only()
    {
        DrugNames.ContainsTerm("warfarin 5 mg", "warfarin").ShouldBeTrue();
        DrugNames.ContainsTerm("losartan", "potassium chloride").ShouldBeFalse();
        DrugNames.ContainsTerm("ferrous ascorbate", "ferrous").ShouldBeTrue();
        DrugNames.ContainsTerm("cefalexin", "cef").ShouldBeFalse();
    }

    [Fact]
    public void Overlap_works_in_either_direction()
    {
        DrugNames.Overlaps("amoxicillin", "amoxicillin clavulanate").ShouldBeTrue();
        DrugNames.Overlaps("amoxicillin clavulanate", "amoxicillin").ShouldBeTrue();
        DrugNames.Overlaps("ampicillin", "amoxicillin").ShouldBeFalse();
    }

    [Fact]
    public void Drug_classes_cover_members_and_labels()
    {
        DrugClasses.ClassesOf("amoxicillin").ShouldHaveSingleItem().Key.ShouldBe(DrugClasses.Penicillins);
        DrugClasses.ClassesForAllergen(DrugNames.Normalize("Sulfa drugs")).ShouldHaveSingleItem().Name.ShouldBe("Sulfonamide antibiotics");
        DrugClasses.ClassesForAllergen(DrugNames.Normalize("NSAIDs")).ShouldHaveSingleItem().Key.ShouldBe(DrugClasses.Nsaids);
        DrugClasses.ClassesForAllergen("aspirin").ShouldHaveSingleItem().Key.ShouldBe(DrugClasses.Nsaids);
    }
}
