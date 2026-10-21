using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// A test's LOINC code (718-7) and its LOINC name in the test master (EHR-08): health records shared through ABDM name the
/// code with it, so a name goes only with the code it names.
/// </summary>
public sealed class LabTestLoincTests
{
    private static readonly SaveLabTestRequestValidator Validator = new();

    [Fact]
    public void A_loinc_code_is_saved_with_its_loinc_name_or_alone()
    {
        Validator.Validate(Test("718-7", "Hemoglobin [Mass/volume] in Blood")).IsValid.ShouldBeTrue();
        Validator.Validate(Test("718-7", null)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_loinc_name_needs_the_code_it_names() =>
        Validator.Validate(Test(null, "Hemoglobin [Mass/volume] in Blood")).Errors.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.PropertyName.ShouldBe(nameof(SaveLabTestRequest.LoincName)),
            e => e.ErrorMessage.ShouldBe("Enter the LOINC code this name belongs to."));

    [Fact]
    public void A_loinc_name_is_at_most_255_characters() =>
        Validator.Validate(Test("718-7", new string('a', 256))).Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(SaveLabTestRequest.LoincName));

    private static SaveLabTestRequest Test(string? loinc, string? loincName) => new(
        Code: "HB", Name: "Haemoglobin", ShortName: null, Section: LabSection.Hematology, SpecimenType: SpecimenType.WholeBlood,
        Container: ContainerType.Edta, Method: null, TatHours: 4, Loinc: loinc, LoincName: loincName, ServiceItemId: null, IsOrderable: false,
        IsPanel: false, ResultType: LabResultType.Numeric, Unit: "g/dL", Decimals: 1, Options: null, InstrumentChannelCodes: null, QcGroup: null,
        DeltaThresholdPercent: null, AllowAutoValidation: false, Preparation: null, DrawSeparately: false, Interpretation: null,
        DefaultReferralLabId: null, DisplayOrder: 0, IsActive: true, Version: null);
}
