using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// A ward's medication order is checked as an e-prescription line of the same medicine is (<see cref="WardMedicationSafety"/>),
/// and the ward's orders are among the patient's current medicines when a doctor e-prescribes
/// (<see cref="PrescriptionSafetyService.CurrentMedicationsAsync"/>). Here the checks run on a ward's medicine as the services
/// hand it to them; the services' reading of the database runs end to end (WardOrderSafetyTests).
/// </summary>
public sealed class WardMedicationSafetyTests
{
    private static WardMedicine Ward(string drug, string? generic, string? strength, string? route, string dose, string frequency) =>
        new(Guid.CreateVersion7(), null, drug, generic, strength, route, dose, frequency);

    [Fact]
    public void A_ward_order_of_amoxicillin_clavulanate_for_a_patient_with_anaphylaxis_to_penicillins_is_blocked()
    {
        var order = Ward("Tab. Amoxicillin + Clavulanic acid 625 mg", "Amoxicillin + Clavulanic acid", "500 mg + 125 mg", "Oral", "1 tablet", "BD");

        var alert = PrescriptionSafetyChecker.Check(new SafetyCheckInput(
                [PrescriptionSafetyService.ToSafetyDrug(order)], [], [new SafetyAllergy("Penicillins", AllergyType.Drug, AllergySeverity.Anaphylaxis, null)], [], []))
            .Alerts.ShouldHaveSingleItem();

        (alert.Kind, alert.IsBlocking).ShouldBe((SafetyAlertKind.ClassAllergy, true));
        alert.Message.ShouldContain("ANAPHYLAXIS");
    }

    [Fact]
    public void An_active_heparin_ward_order_is_a_current_medicine_an_aspirin_prescription_interacts_with()
    {
        var heparin = PrescriptionSafetyService.ToSafetyDrug(Ward("Inj. Heparin", "Heparin sodium", "5000 IU/ml", "SC", "5000 IU", "BD"), isCurrent: true);
        var aspirin = new SafetyDrug(Guid.CreateVersion7(), "Tab. Ecosprin 75", "Aspirin", "75 mg", "Oral", "1 tab", DoseFrequency.OD, null, null, false);

        var alert = PrescriptionSafetyChecker.Check(new SafetyCheckInput([aspirin], [heparin], [],
            [new SafetyInteractionRule("heparin", "aspirin", InteractionSeverity.Major, "Additive bleeding risk", "Avoid")], [])).Alerts.ShouldHaveSingleItem();

        (alert.Kind, alert.IsBlocking).ShouldBe((SafetyAlertKind.Interaction, true));
        alert.Message.ShouldContain("Inj. Heparin (already prescribed on the ward chart)");
        alert.ItemIds.ShouldBe([aspirin.Id]);
    }

    [Theory]
    [InlineData("BD", DoseFrequency.BD, null)]
    [InlineData("b.d.", DoseFrequency.BD, null)]
    [InlineData("Once daily", DoseFrequency.OD, null)]
    [InlineData("TID", DoseFrequency.TDS, null)]
    [InlineData("QDS", DoseFrequency.QID, null)]
    [InlineData("Nocte", DoseFrequency.HS, null)]
    [InlineData("PRN", DoseFrequency.SOS, null)]
    [InlineData("STAT", DoseFrequency.STAT, null)]
    [InlineData("weekly", DoseFrequency.Weekly, null)]
    [InlineData("1-0-1", DoseFrequency.Custom, "1-0-1")]
    [InlineData(" Q6H ", DoseFrequency.Custom, "Q6H")]
    [InlineData("As directed", DoseFrequency.Custom, "As directed")]
    public void A_ward_frequency_is_read_as_a_prescription_line_has_it(string text, DoseFrequency frequency, string? custom) =>
        WardFrequency.Parse(text).ShouldBe((frequency, custom));

    [Fact]
    public void The_maximum_dose_reads_a_ward_frequency_as_it_reads_a_prescription_lines()
    {
        // Paracetamol 1 g by mouth every 4 hours: 6 g a day, above the 4 g maximum.
        var order = Ward("Tab. Paracetamol 500 mg", "Paracetamol", "500 mg", "Oral", "1 g", "Q4H");

        var alert = PrescriptionSafetyChecker.Check(new SafetyCheckInput([PrescriptionSafetyService.ToSafetyDrug(order)], [], [], [],
            [new SafetyDoseLimit("paracetamol", 4000m, "oral")])).Alerts.ShouldHaveSingleItem();

        (alert.Kind, alert.IsBlocking).ShouldBe((SafetyAlertKind.MaxDose, true));
        alert.Message.ShouldContain("6000 mg/day");
    }

    [Fact]
    public void A_medicine_picked_from_the_drug_master_is_named_and_its_form_read_as_the_master_has_them()
    {
        var order = new WardMedicine(Guid.CreateVersion7(), Guid.CreateVersion7(), "Monocef 1 g", null, "1 g", null, "1 g", "BD");

        var drug = PrescriptionSafetyService.ToSafetyDrug(order, genericName: "Ceftriaxone", dosageForm: "Powder for injection");

        drug.Components.ShouldContain("ceftriaxone");
        PrescriptionSafetyChecker.Check(new SafetyCheckInput([drug], [], [], [], [])).Alerts.ShouldHaveSingleItem()
            .Message.ShouldBe("Monocef 1 g is an injection: choose how it is given (IV, IM or SC).");
    }
}
