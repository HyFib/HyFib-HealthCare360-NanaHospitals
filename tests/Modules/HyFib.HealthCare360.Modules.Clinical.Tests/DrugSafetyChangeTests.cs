using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// Which changes to the prescribing-safety tables wait for the pharmacy and therapeutics committee: those that switch off or
/// weaken an alert prescribers get now. Adding or strengthening one, or changing only its words, is saved at once.
/// </summary>
public sealed class DrugSafetyChangeTests
{
    private static readonly InteractionState Major = new("aspirin", "warfarin", InteractionSeverity.Major, "Bleeding risk", "Avoid", IsActive: true);
    private static readonly DoseLimitState Paracetamol = new("paracetamol", 4000m, "oral", null, IsActive: true);

    [Fact]
    public void An_interaction_switched_off_made_less_severe_or_moved_to_other_drugs_loosens()
    {
        DrugSafetyChanges.Loosens(Major, Major with { IsActive = false }).ShouldBeTrue();
        DrugSafetyChanges.Loosens(Major, Major with { Severity = InteractionSeverity.Moderate }).ShouldBeTrue();
        DrugSafetyChanges.Loosens(Major with { Severity = InteractionSeverity.Moderate }, Major with { Severity = InteractionSeverity.Minor }).ShouldBeTrue();
        DrugSafetyChanges.Loosens(Major, Major with { DrugB = "heparin" }).ShouldBeTrue("warfarin with aspirin would no longer alert");
    }

    [Fact]
    public void An_interaction_made_more_severe_switched_on_or_reworded_is_saved_at_once()
    {
        var moderate = Major with { Severity = InteractionSeverity.Moderate };
        DrugSafetyChanges.Loosens(moderate, Major).ShouldBeFalse();
        DrugSafetyChanges.Loosens(Major with { IsActive = false }, Major).ShouldBeFalse();
        DrugSafetyChanges.Loosens(Major, Major with { Effect = "Major bleeding", Management = "Never together" }).ShouldBeFalse();

        // Switched off, it alerts nobody: changing it loosens nothing, and switching it on again is saved at once.
        DrugSafetyChanges.Loosens(Major with { IsActive = false }, moderate with { IsActive = false, DrugB = "heparin" }).ShouldBeFalse();
    }

    [Fact]
    public void A_maximum_dose_raised_switched_off_or_checking_fewer_routes_loosens()
    {
        DrugSafetyChanges.Loosens(Paracetamol, Paracetamol with { MaxDailyDoseMg = 4001m }).ShouldBeTrue();
        DrugSafetyChanges.Loosens(Paracetamol, Paracetamol with { IsActive = false }).ShouldBeTrue();
        DrugSafetyChanges.Loosens(Paracetamol, Paracetamol with { GenericName = "ibuprofen" }).ShouldBeTrue();
        DrugSafetyChanges.Loosens(Paracetamol, Paracetamol with { Route = "iv" }).ShouldBeTrue("the oral route is no longer checked");
        DrugSafetyChanges.Loosens(Paracetamol with { Route = null }, Paracetamol).ShouldBeTrue("only the oral route is checked, no longer every route");
    }

    [Fact]
    public void A_maximum_dose_lowered_widened_to_every_route_or_switched_on_is_saved_at_once()
    {
        DrugSafetyChanges.Loosens(Paracetamol, Paracetamol with { MaxDailyDoseMg = 3000m }).ShouldBeFalse();
        DrugSafetyChanges.Loosens(Paracetamol, Paracetamol with { Route = null }).ShouldBeFalse();
        DrugSafetyChanges.Loosens(Paracetamol with { IsActive = false }, Paracetamol with { MaxDailyDoseMg = 9000m }).ShouldBeFalse();
        DrugSafetyChanges.Loosens(Paracetamol, Paracetamol with { Notes = "Hepatic impairment: 2 g" }).ShouldBeFalse();
    }

    [Fact]
    public void The_approver_reads_what_the_change_switches_off_in_words()
    {
        Major.Words().ShouldBe("aspirin + warfarin: Major, blocks signing");
        (Major with { IsActive = false }).Words().ShouldBe("aspirin + warfarin: switched off (not checked)");
        DrugSafetyChanges.Consequences(Major, Major with { Severity = InteractionSeverity.Moderate })
            .ShouldBe("Prescribing aspirin with warfarin no longer blocks signing: the doctor acknowledges a warning instead, for every prescriber in the hospital.");
        DrugSafetyChanges.Consequences(Major, Major with { IsActive = false })
            .ShouldBe("Prescribing aspirin with warfarin no longer blocks signing, nor warns, for every prescriber in the hospital.");

        Paracetamol.Words().ShouldBe("paracetamol, oral: at most 4,000 mg a day");
        DrugSafetyChanges.Consequences(Paracetamol, Paracetamol with { MaxDailyDoseMg = 6000m })
            .ShouldBe("A daily dose of paracetamol up to 6,000 mg (above 4,000 mg today) is signed without the block, for every prescriber in the hospital.");
        DrugSafetyChanges.Consequences(Paracetamol, Paracetamol with { IsActive = false })
            .ShouldBe("A prescription with any daily dose of paracetamol is signed without the maximum-dose block, for every prescriber in the hospital.");
    }
}
