using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class PrescriptionSafetyCheckerTests
{
    private static readonly SafetyInteractionRule[] Rules =
    [
        new("warfarin", "aspirin", InteractionSeverity.Major, "Bleeding risk", "Avoid"),
        new("simvastatin", "clarithromycin", InteractionSeverity.Major, "Rhabdomyolysis", "Pause simvastatin"),
        new("clopidogrel", "omeprazole", InteractionSeverity.Moderate, "Reduced antiplatelet effect", "Use pantoprazole"),
        new("paracetamol", "warfarin", InteractionSeverity.Minor, "INR may rise", "Monitor INR"),
    ];

    private static readonly SafetyDoseLimit[] Limits =
    [
        new("paracetamol", 4000m, "oral"),
        new("ibuprofen", 2400m, "oral"),
    ];

    private static SafetyDrug Drug(
        string name,
        string? generic,
        string? strength,
        string dose,
        DoseFrequency frequency = DoseFrequency.OD,
        string? custom = null,
        bool controlled = false,
        bool current = false) =>
        new(Guid.CreateVersion7(), name, generic, strength, "Oral", dose, frequency, custom, null, controlled, current, current ? "NHRX260000001" : null);

    private static SafetyCheckResult Check(
        IReadOnlyList<SafetyDrug> items,
        IReadOnlyList<SafetyAllergy>? allergies = null,
        IReadOnlyList<SafetyDrug>? current = null) =>
        PrescriptionSafetyChecker.Check(new SafetyCheckInput(items, current ?? [], allergies ?? [], Rules, Limits));

    private static SafetyAllergy DrugAllergy(string allergen, AllergySeverity severity = AllergySeverity.Moderate) =>
        new(allergen, AllergyType.Drug, severity, "rash");

    [Fact]
    public void Clean_prescription_has_no_alerts()
    {
        var result = Check([Drug("Paracetamol 500", "Paracetamol", "500 mg", "1 tab", DoseFrequency.TDS)]);

        result.Alerts.ShouldBeEmpty();
        result.IsBlocked.ShouldBeFalse();
    }

    [Fact]
    public void Direct_allergy_blocks_signing()
    {
        var item = Drug("Mox 500", "Amoxicillin", "500 mg", "1 cap", DoseFrequency.TDS);

        var alert = Check([item], [DrugAllergy("Amoxicillin", AllergySeverity.Anaphylaxis)]).Alerts.ShouldHaveSingleItem();

        alert.Kind.ShouldBe(SafetyAlertKind.Allergy);
        alert.Severity.ShouldBe(SafetyAlertSeverity.Major);
        alert.IsBlocking.ShouldBeTrue();
        alert.Message.ShouldContain("ANAPHYLAXIS");
        alert.ItemIds.ShouldBe([item.Id]);
    }

    [Fact]
    public void Penicillin_allergy_blocks_an_amoxicillin_combination()
    {
        var alert = Check([Drug("Augmentin 625", "Amoxycillin + Potassium Clavulanate", "500 mg + 125 mg", "1 tab", DoseFrequency.BD)],
            [DrugAllergy("Penicillin")]).Alerts.ShouldHaveSingleItem();

        alert.Kind.ShouldBe(SafetyAlertKind.ClassAllergy);
        alert.IsBlocking.ShouldBeTrue();
        alert.Message.ShouldContain("Penicillins");
    }

    [Fact]
    public void Aspirin_allergy_covers_other_nsaids() =>
        Check([Drug("Brufen 400", "Ibuprofen", "400 mg", "1 tab", DoseFrequency.TDS)], [DrugAllergy("Aspirin")])
            .Alerts.ShouldHaveSingleItem().Kind.ShouldBe(SafetyAlertKind.ClassAllergy);

    [Fact]
    public void Penicillin_allergy_warns_about_cephalosporins_without_blocking()
    {
        var result = Check([Drug("Taxim-O 200", "Cefixime", "200 mg", "1 tab", DoseFrequency.BD)], [DrugAllergy("Penicillin")]);

        var alert = result.Alerts.ShouldHaveSingleItem();
        alert.Kind.ShouldBe(SafetyAlertKind.CrossSensitivity);
        alert.Severity.ShouldBe(SafetyAlertSeverity.Moderate);
        alert.IsBlocking.ShouldBeFalse();
        result.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public void Food_and_environmental_allergies_are_not_matched_to_medicines() =>
        Check([Drug("Arachis oil", "Peanut oil", null, "5 ml")], [new SafetyAllergy("Peanut", AllergyType.Food, AllergySeverity.Severe, null)])
            .Alerts.ShouldBeEmpty();

    [Fact]
    public void Major_interaction_between_new_lines_blocks_signing()
    {
        var warfarin = Drug("Warf 5", "Warfarin Sodium", "5 mg", "1 tab");
        var aspirin = Drug("Ecosprin 75", "Aspirin", "75 mg", "1 tab");

        var alert = Check([warfarin, aspirin]).Alerts.ShouldHaveSingleItem();

        alert.Kind.ShouldBe(SafetyAlertKind.Interaction);
        alert.IsBlocking.ShouldBeTrue();
        alert.ItemIds.ShouldBe([warfarin.Id, aspirin.Id]);
        alert.Detail.ShouldBe("Avoid");
    }

    [Fact]
    public void Interactions_with_current_medicines_are_checked()
    {
        var simvastatin = Drug("Zocor 20", "Simvastatin", "20 mg", "1 tab");
        var clarithromycin = Drug("Claribid 500", "Clarithromycin", "500 mg", "1 tab", DoseFrequency.BD, current: true);

        var alert = Check([simvastatin], current: [clarithromycin]).Alerts.ShouldHaveSingleItem();

        alert.IsBlocking.ShouldBeTrue();
        alert.ItemIds.ShouldBe([simvastatin.Id]);
        alert.Message.ShouldContain("already prescribed on NHRX260000001");
    }

    [Fact]
    public void Moderate_interaction_is_a_warning() =>
        Check([Drug("Clopilet 75", "Clopidogrel", "75 mg", "1 tab"), Drug("Omez 20", "Omeprazole", "20 mg", "1 cap")])
            .Alerts.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
                a => a.Severity.ShouldBe(SafetyAlertSeverity.Moderate),
                a => a.IsBlocking.ShouldBeFalse());

    [Fact]
    public void Interaction_terms_match_synonyms()
    {
        var result = Check([Drug("Tylenol", "Acetaminophen", "500 mg", "1 tab")], current: [Drug("Warf 5", "Warfarin", "5 mg", "1 tab", current: true)]);

        var alert = result.Alerts.ShouldHaveSingleItem();
        alert.Severity.ShouldBe(SafetyAlertSeverity.Minor);
        alert.IsBlocking.ShouldBeFalse();
    }

    [Fact]
    public void Same_ingredient_in_two_products_is_duplicate_therapy()
    {
        var result = Check(
        [
            Drug("Dolo 650", "Paracetamol", "650 mg", "1 tab", DoseFrequency.TDS),
            Drug("Combiflam", "Ibuprofen + Paracetamol", "400 mg + 325 mg", "1 tab", DoseFrequency.TDS),
        ]);

        var alert = result.Alerts.ShouldHaveSingleItem();
        alert.Kind.ShouldBe(SafetyAlertKind.DuplicateTherapy);
        alert.Message.ShouldContain("paracetamol");
        alert.IsBlocking.ShouldBeFalse();
    }

    [Fact]
    public void Two_nsaids_are_flagged() =>
        Check([Drug("Brufen 400", "Ibuprofen", "400 mg", "1 tab", DoseFrequency.TDS), Drug("Voveran 50", "Diclofenac Sodium", "50 mg", "1 tab", DoseFrequency.BD)])
            .Alerts.ShouldHaveSingleItem().Message.ShouldStartWith("Two NSAIDs");

    [Fact]
    public void Daily_dose_is_summed_across_products_and_blocks_above_the_maximum()
    {
        var result = Check(
        [
            Drug("Paracetamol 1000", "Paracetamol", "1000 mg", "1 tab", DoseFrequency.QID),
            Drug("Combiflam", "Ibuprofen + Paracetamol", "400 mg + 325 mg", "1 tab", DoseFrequency.TDS),
        ]);

        var maxDose = result.Alerts.Single(a => a.Kind == SafetyAlertKind.MaxDose);
        maxDose.IsBlocking.ShouldBeTrue();
        maxDose.Message.ShouldContain("4975 mg/day");
        result.Alerts.ShouldContain(a => a.Kind == SafetyAlertKind.DuplicateTherapy);
        result.Alerts.First().IsBlocking.ShouldBeTrue();
    }

    [Fact]
    public void Current_medicines_count_towards_the_daily_total()
    {
        var result = Check([Drug("Dolo 650", "Paracetamol", "650 mg", "1 tab", DoseFrequency.TDS)],
            current: [Drug("Paracetamol 1000", "Paracetamol", "1000 mg", "1 tab", DoseFrequency.TDS, current: true)]);

        result.Alerts.Single(a => a.Kind == SafetyAlertKind.MaxDose).Message.ShouldContain("4950 mg/day");
    }

    [Fact]
    public void Doses_within_the_limit_pass()
    {
        Check([Drug("Crocin 500", "Paracetamol", "500 mg", "2 tab", DoseFrequency.TDS)]).Alerts.ShouldBeEmpty();
        Check([Drug("Calpol syrup", "Paracetamol", "250 mg/5 ml", "10 ml", DoseFrequency.QID)]).Alerts.ShouldBeEmpty();
    }

    [Fact]
    public void Unverifiable_dose_of_a_limited_drug_is_reported_not_skipped()
    {
        var alert = Check([Drug("Paracetamol syrup", "Paracetamol", "250 mg", "5 ml", DoseFrequency.TDS)]).Alerts.ShouldHaveSingleItem();

        alert.Kind.ShouldBe(SafetyAlertKind.DoseUnverified);
        alert.IsBlocking.ShouldBeFalse();
    }

    [Fact]
    public void Daily_methotrexate_is_blocked_but_weekly_is_allowed()
    {
        var daily = Check([Drug("Folitrax 7.5", "Methotrexate", "7.5 mg", "1 tab", DoseFrequency.OD)]).Alerts.ShouldHaveSingleItem();
        daily.Kind.ShouldBe(SafetyAlertKind.HighAlertMedication);
        daily.IsBlocking.ShouldBeTrue();

        Check([Drug("Folitrax 7.5", "Methotrexate", "7.5 mg", "1 tab", DoseFrequency.Weekly)]).Alerts.ShouldBeEmpty();
        Check([Drug("Folitrax 7.5", "Methotrexate", "7.5 mg", "1 tab", DoseFrequency.Custom, "once weekly")]).Alerts.ShouldBeEmpty();
    }

    [Fact]
    public void Controlled_drugs_are_announced_but_confirmed_separately()
    {
        var result = Check([Drug("Alprax 0.25", "Alprazolam", "0.25 mg", "1 tab", DoseFrequency.HS, controlled: true)]);

        var alert = result.Alerts.ShouldHaveSingleItem();
        alert.Kind.ShouldBe(SafetyAlertKind.ControlledDrug);
        alert.Severity.ShouldBe(SafetyAlertSeverity.Info);
        result.Warnings.ShouldBeEmpty();
        result.IsBlocked.ShouldBeFalse();
    }

    private static SafetyDrug Line(string name, string? dosageForm, string? route) =>
        new(Guid.CreateVersion7(), name, "Ceftriaxone", "1 g", route, "1 g", DoseFrequency.BD, null, null, false, DosageForm: dosageForm);

    [Fact]
    public void An_injection_with_no_route_or_given_by_mouth_blocks_signing()
    {
        // As in the production-style run: the drug master's ceftriaxone (powder for injection) was signed as oral.
        var oral = Check([Line("Ceftriaxone 1 g Injection", "Powder for injection", "Oral")]).Alerts.Single(a => a.Kind == SafetyAlertKind.Route);
        oral.IsBlocking.ShouldBeTrue();
        oral.Severity.ShouldBe(SafetyAlertSeverity.Major);
        oral.Message.ShouldBe("Ceftriaxone 1 g Injection is an injection, but its route is Oral.");

        Check([Line("Ceftriaxone 1 g Injection", "Powder for injection", null)]).Alerts.Single(a => a.Kind == SafetyAlertKind.Route).Message
            .ShouldBe("Ceftriaxone 1 g Injection is an injection: choose how it is given (IV, IM or SC).");

        // The form says injection though the name does not.
        Check([Line("Cefotaxime 1 g", "Injection", "Oral")]).IsBlocked.ShouldBeTrue();
        foreach (var route in new[] { "IV", "IM", "SC", "Other" })
        {
            Check([Line("Ceftriaxone 1 g Injection", "Powder for injection", route)]).Alerts.ShouldNotContain(a => a.Kind == SafetyAlertKind.Route, route);
        }
    }

    [Fact]
    public void A_medicine_taken_by_mouth_given_by_needle_blocks_signing_and_a_name_stands_in_for_a_missing_form()
    {
        Check([Line("Paracetamol 500 mg Tablet", null, "IV")]).Alerts.Single(a => a.Kind == SafetyAlertKind.Route).Message
            .ShouldBe("Paracetamol 500 mg Tablet is taken by mouth, but its route is IV.");
        Check([Line("Inj. Pantoprazole 40", null, "Oral")]).IsBlocked.ShouldBeTrue();

        // Neither the form nor the name says how it is given: the route is the prescriber's.
        Check([Line("Pan 40", null, "IV")]).Alerts.ShouldNotContain(a => a.Kind == SafetyAlertKind.Route);
        Check([Line("Normal saline 0.9%", "Solution", "Oral")]).Alerts.ShouldNotContain(a => a.Kind == SafetyAlertKind.Route);
    }

    [Theory]
    [InlineData("Tablet", DosageFormKind.Oral)]
    [InlineData("Tablet (enteric-coated)", DosageFormKind.Oral)]
    [InlineData("Soft gelatin capsule", DosageFormKind.Oral)]
    [InlineData("Powder for oral suspension", DosageFormKind.Oral)]
    [InlineData("Oral solution", DosageFormKind.Oral)]
    [InlineData("Injection", DosageFormKind.Injection)]
    [InlineData("Powder for injection", DosageFormKind.Injection)]
    [InlineData("IV infusion", DosageFormKind.Injection)]
    [InlineData("Metered-dose inhaler", DosageFormKind.Local)]
    [InlineData("Nebuliser suspension", DosageFormKind.Local)]
    [InlineData("Cream", DosageFormKind.Local)]
    [InlineData("Jelly", DosageFormKind.Local)]
    [InlineData("Solution", DosageFormKind.Unknown)]
    [InlineData("", DosageFormKind.Unknown)]
    public void The_dosage_forms_of_the_drug_master_are_read_as_they_are_given(string form, DosageFormKind kind) =>
        DosageForms.Classify(form).ShouldBe(kind);
}
