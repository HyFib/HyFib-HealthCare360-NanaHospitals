using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>Telemedicine Practice Guidelines 2020: Schedule X and NDPS medicines are never prescribed in a teleconsultation.</summary>
public sealed class TelemedicinePrescribingTests
{
    [Fact]
    public void Schedule_X_and_NDPS_medicines_are_on_the_prohibited_list_and_others_are_not()
    {
        TelemedicinePrescribing.Prohibited([
            new("Paracetamol 500 mg", DrugSchedule.None, false),
            new("Azithromycin 500 mg", DrugSchedule.H1, false),
            new("Alprazolam 0.5 mg", DrugSchedule.X, false),
            new("Tramadol 50 mg", DrugSchedule.H1, true),
            new("Alprazolam 0.5 mg", DrugSchedule.X, false),
        ]).ShouldBe(["Alprazolam 0.5 mg", "Tramadol 50 mg"]);

        TelemedicinePrescribing.Prohibited([new("Cetirizine 10 mg", DrugSchedule.None, false)]).ShouldBeEmpty();
    }

    [Fact]
    public void The_refusal_names_the_medicines_and_what_to_do()
    {
        var refusal = TelemedicinePrescribing.Refusal(["Alprazolam 0.5 mg"]);

        refusal.Code.ShouldBe("prescription.telemedicine_prohibited");
        refusal.Message.ShouldStartWith("Alprazolam 0.5 mg cannot be prescribed in a teleconsultation");
        refusal.Message.ShouldContain("See the patient in person");
    }
}
