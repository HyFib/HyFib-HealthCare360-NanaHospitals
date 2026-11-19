using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>A ward order written over a blocking safety alert gives the clinical reason, as an e-prescription's signing does.</summary>
public sealed class MedicationOrderRequestTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Tolerated", false)]
    [InlineData("  Tolerated   ", false)]
    [InlineData("Tolerated amoxicillin in 2025 without a reaction", true)]
    public void The_reason_for_ordering_over_a_blocking_alert_takes_at_least_ten_characters(string? reason, bool valid) =>
        new AddMedicationOrderRequestValidator(TimeProvider.System)
            .Validate(new AddMedicationOrderRequest(
                DrugItemId: null, DrugName: "Tab. Amoxicillin + Clavulanic acid 625 mg", GenericName: "Amoxicillin + Clavulanic acid", Strength: "500 mg + 125 mg",
                Dose: "1 tablet", Route: "Oral", Frequency: "BD", DurationDays: 5, StartAt: null, Instructions: null, IsControlledDrug: false,
                OrderedByPractitionerId: null, OverrideReason: reason))
            .IsValid.ShouldBe(valid);
}
