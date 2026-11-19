using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// The route check (<see cref="RouteCheck"/>) on ward orders as the ward writes them: the staff app's routes (Oral, IV, IM,
/// SC, Sublingual, Topical, Inhalation, Nebulisation, Rectal, Nasogastric), the ward's abbreviations (PO, SL, PR, NG,
/// Inhaled, Nebulised), and the drug master's dosage forms, or none for a medicine typed in. It refuses only a route the
/// form contradicts (a medicine taken by mouth given by needle, an injection given by mouth or another route no injection
/// is given by) and an order with no route. An injection's solution nebulised or put into the nose is ordinary ward
/// practice, and a depot injection the master calls an "injectable suspension" is an injection.
/// </summary>
public sealed class WardOrderRouteTests
{
    /// <summary>A ward order as the safety check sees it: the master's dosage form when it was picked from the master and has one.</summary>
    private static SafetyDrug Order(string name, string? dosageForm, string? route) =>
        PrescriptionSafetyService.ToSafetyDrug(new WardMedicine(Guid.CreateVersion7(), null, name, null, null, route, "1", "BD"), dosageForm: dosageForm);

    private static List<SafetyAlert> RouteAlerts(string name, string? dosageForm, string? route) => [.. RouteCheck.Run([Order(name, dosageForm, route)])];

    [Theory]
    [InlineData("Paracetamol 500 mg Tablet", "Tablet", "Oral")]
    [InlineData("Paracetamol 500 mg Tablet", "Tablet", "PO")]
    [InlineData("Paracetamol 500 mg Tablet", "Tablet", "Nasogastric")]
    [InlineData("Paracetamol 500 mg Tablet", "Tablet", "NG")]
    [InlineData("Isosorbide dinitrate 5 mg Tablet", "Tablet", "Sublingual")]
    [InlineData("Isosorbide dinitrate 5 mg Tablet", "Tablet", "SL")]
    [InlineData("Misoprostol 200 mcg Tablet", "Tablet", "Rectal")]
    [InlineData("Misoprostol 200 mcg Tablet", "Tablet", "PR")]
    [InlineData("Pantoprazole 40 mg Tablet", "Tablet (gastro-resistant)", "Oral")]
    [InlineData("Amoxicillin 500 mg Capsule", "Capsule", "PO")]
    [InlineData("Cholecalciferol 60000 IU Capsule", "Soft gelatin capsule", "Oral")]
    [InlineData("Ondansetron 4 mg Tablet", "Orally disintegrating tablet", "SL")]
    [InlineData("Paracetamol 120 mg/5 ml Suspension", "Oral suspension", "NG")]
    [InlineData("Lactulose 10 g/15 ml Solution", "Oral solution", "Nasogastric")]
    [InlineData("Oral Rehydration Salts 21.8 g", "Powder for oral solution", "Oral")]
    [InlineData("Ceftriaxone 1 g Injection", "Powder for injection", "IV")]
    [InlineData("Ondansetron 2 mg/ml Injection", "Injection", "IM")]
    [InlineData("Heparin 5000 IU/ml Injection", "Injection", "SC")]
    [InlineData("Morphine 10 mg/ml Injection", "Injection", "Intravenous")]
    [InlineData("Ringer Lactate IV Infusion 500 ml", "IV infusion", "IV")]
    [InlineData("Silver Sulfadiazine 1% Cream", "Cream", "Topical")]
    [InlineData("Lignocaine 2% Jelly", "Jelly", "Topical")]
    [InlineData("Povidone-Iodine 5% Solution", "Solution", "Topical")]
    [InlineData("Salbutamol 100 mcg Inhaler", "Metered-dose inhaler", "Inhalation")]
    [InlineData("Salbutamol 100 mcg Inhaler", "Metered-dose inhaler", "Inhaled")]
    [InlineData("Budesonide 0.5 mg Respules", "Nebuliser suspension", "Nebulisation")]
    [InlineData("Budesonide 0.5 mg Respules", "Nebuliser suspension", "Nebulised")]
    [InlineData("Paracetamol 170 mg Suppository", "Suppository", "PR")]
    [InlineData("Moxifloxacin 0.5% Eye drops", "Eye drops", "Topical")]
    public void A_ward_order_by_a_route_its_dosage_form_is_given_by_raises_no_route_alert(string name, string form, string route) =>
        RouteAlerts(name, form, route).ShouldBeEmpty($"{name} ({form}) by {route}");

    [Theory]
    [InlineData("Adrenaline 1 mg/ml Injection", "Injection", "Nebulisation")]
    [InlineData("Acetylcysteine 200 mg/ml Injection", "Injection", "Nebulisation")]
    [InlineData("Colistimethate 1 MIU Injection", "Powder for injection", "Inhalation")]
    [InlineData("Sodium Chloride 3% Injection", "Injection", "Nebulization")]
    [InlineData("Midazolam 1 mg/ml Injection", "Injection", "Nasal")]
    public void An_injection_nebulised_or_given_into_the_nose_raises_no_route_alert(string name, string form, string route) =>
        // Adrenaline for croup or stridor after extubation, acetylcysteine, colistin and hypertonic saline are nebulised from
        // the injection, and midazolam put into the nose for a seizure. Refused, the order would be written with another
        // route to get it on the chart: adrenaline charted as IV.
        RouteAlerts(name, form, route).ShouldBeEmpty($"{name} by {route}");

    [Theory]
    [InlineData("Medroxyprogesterone acetate 150 mg/ml", "Injectable suspension", "IM")]
    [InlineData("Triamcinolone acetonide 40 mg/ml", "Injectable suspension", "IM")]
    [InlineData("Propofol 1% (10 mg/ml)", "Injectable emulsion", "IV")]
    public void A_form_the_master_calls_injectable_is_an_injection(string name, string form, string route)
    {
        // A depot injection is often an "injectable suspension": read as a suspension taken by mouth, its IM order was refused.
        DosageForms.Classify(form).ShouldBe(DosageFormKind.Injection);
        RouteAlerts(name, form, route).ShouldBeEmpty($"{name} ({form}) by {route}");
    }

    [Theory]
    [InlineData("Ceftriaxone 1 g Injection", "Powder for injection", "Oral", "Ceftriaxone 1 g Injection is an injection, but its route is Oral.")]
    [InlineData("Ceftriaxone 1 g Injection", "Powder for injection", "PO", "Ceftriaxone 1 g Injection is an injection, but its route is PO.")]
    [InlineData("Medroxyprogesterone acetate 150 mg/ml", "Injectable suspension", "Oral", "Medroxyprogesterone acetate 150 mg/ml is an injection, but its route is Oral.")]
    [InlineData("Paracetamol 500 mg Tablet", "Tablet", "IV", "Paracetamol 500 mg Tablet is taken by mouth, but its route is IV.")]
    [InlineData("Paracetamol 120 mg/5 ml Suspension", "Oral suspension", "IM", "Paracetamol 120 mg/5 ml Suspension is taken by mouth, but its route is IM.")]
    public void A_route_the_dosage_form_contradicts_still_blocks_the_order(string name, string form, string route, string message)
    {
        var alert = RouteAlerts(name, form, route).ShouldHaveSingleItem();

        (alert.IsBlocking, alert.Message).ShouldBe((true, message));
    }

    [Theory]
    [InlineData("Ceftriaxone 1 g IV", "IV")]
    [InlineData("Paracetamol 1 g IV", "IV")]
    [InlineData("Pan 40", "IV")]
    [InlineData("Pan 40", "Oral")]
    [InlineData("Heparin 5000 IU", "SC")]
    [InlineData("Insulin Actrapid", "SC")]
    [InlineData("Duolin", "Nebulisation")]
    [InlineData("Normal saline 0.9%", "Nebulisation")]
    [InlineData("Potassium chloride 15%", "Nasogastric")]
    [InlineData("Diazepam", "Rectal")]
    public void A_ward_order_typed_by_a_name_that_names_no_form_raises_no_route_alert_whatever_its_route(string name, string route)
    {
        // Typed in, with no item of the master, so no dosage form: the name is all the check reads, and it says nothing.
        var order = Order(name, null, route);
        order.DosageForm.ShouldBeNull();

        PrescriptionSafetyChecker.Check(new SafetyCheckInput([order], [], [], [], [])).Alerts.ShouldNotContain(a => a.Kind == SafetyAlertKind.Route);
    }

    [Fact]
    public void A_medicine_with_no_dosage_form_is_judged_by_what_its_name_says()
    {
        // Typed in, or picked from a master item with no form: "Inj." or "Tablet" in the name settles the form.
        RouteAlerts("Inj. Ceftriaxone 1 g", null, "IV").ShouldBeEmpty();
        RouteAlerts("Ceftriaxone 1 g Injection", null, "IM").ShouldBeEmpty();
        RouteAlerts("Tab. Amoxicillin + Clavulanic acid 625 mg", null, "Oral").ShouldBeEmpty();
        RouteAlerts("Inj. Adrenaline 1 mg/ml", null, "Nebulisation").ShouldBeEmpty();
        RouteAlerts("Inj. Pantoprazole 40", null, "Oral").ShouldHaveSingleItem().IsBlocking.ShouldBeTrue();
        RouteAlerts("Tab. Paracetamol 650", null, "IV").ShouldHaveSingleItem().IsBlocking.ShouldBeTrue();
    }

    [Fact]
    public void A_ward_order_with_no_route_is_asked_for_one_whatever_its_form()
    {
        RouteAlerts("Pan 40", null, null).ShouldHaveSingleItem().Message.ShouldBe("Pan 40 has no route: choose how it is given.");
        RouteAlerts("Ceftriaxone 1 g Injection", "Powder for injection", " ").ShouldHaveSingleItem().Message
            .ShouldBe("Ceftriaxone 1 g Injection is an injection: choose how it is given (IV, IM or SC).");
    }
}
