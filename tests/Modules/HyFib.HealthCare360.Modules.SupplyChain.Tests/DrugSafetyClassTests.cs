using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>
/// How strictly a drug is handled (its schedule, the NDPS flag and the high-alert flag), and what counts as handling it
/// less strictly: on a drug already in the master that waits for a pharmacist's approval, while handling it more strictly
/// applies at once.
/// </summary>
public sealed class DrugSafetyClassTests
{
    private static DrugSafetyClass Class(DrugSchedule schedule, bool controlled = false, bool highAlert = false) => new(schedule, controlled, highAlert);

    [Theory]
    // No longer controlled, no longer high-alert, a lower schedule: each alone is less strict.
    [InlineData(DrugSchedule.X, true, false, DrugSchedule.X, false, false)]
    [InlineData(DrugSchedule.H, false, true, DrugSchedule.H, false, false)]
    [InlineData(DrugSchedule.X, false, false, DrugSchedule.H1, false, false)]
    [InlineData(DrugSchedule.H1, false, false, DrugSchedule.H, false, false)]
    [InlineData(DrugSchedule.H, false, false, DrugSchedule.Otc, false, false)]
    [InlineData(DrugSchedule.H, false, false, DrugSchedule.None, false, false)]
    // Less strict in one way is less strict, whatever is made stricter at the same time.
    [InlineData(DrugSchedule.H, true, false, DrugSchedule.X, false, true)]
    public void A_drug_handled_less_strictly_in_any_way_is_weakened(
        DrugSchedule fromSchedule, bool fromControlled, bool fromHighAlert, DrugSchedule toSchedule, bool toControlled, bool toHighAlert) =>
        Class(fromSchedule, fromControlled, fromHighAlert).IsWeakenedBy(Class(toSchedule, toControlled, toHighAlert)).ShouldBeTrue();

    [Theory]
    [InlineData(DrugSchedule.H, false, false, DrugSchedule.H, false, false)]
    [InlineData(DrugSchedule.H, false, false, DrugSchedule.H1, true, true)]
    [InlineData(DrugSchedule.None, false, false, DrugSchedule.X, false, false)]
    // Over the counter, Schedule G and none prescribe and register nothing: moving among them is not less strict.
    [InlineData(DrugSchedule.G, false, false, DrugSchedule.Otc, false, false)]
    [InlineData(DrugSchedule.Otc, false, false, DrugSchedule.None, false, false)]
    public void A_drug_handled_as_strictly_or_more_strictly_is_not_weakened(
        DrugSchedule fromSchedule, bool fromControlled, bool fromHighAlert, DrugSchedule toSchedule, bool toControlled, bool toHighAlert) =>
        Class(fromSchedule, fromControlled, fromHighAlert).IsWeakenedBy(Class(toSchedule, toControlled, toHighAlert)).ShouldBeFalse();

    [Fact]
    public void The_class_is_read_from_the_item_and_written_back()
    {
        var item = new Item { Type = ItemType.Drug, Schedule = DrugSchedule.H1, IsControlled = true, IsHighAlert = true };
        DrugSafetyClass.Of(item).ShouldBe(Class(DrugSchedule.H1, controlled: true, highAlert: true));

        Class(DrugSchedule.H).ApplyTo(item);

        (item.Schedule, item.IsControlled, item.IsHighAlert).ShouldBe((DrugSchedule.H, false, false));
    }

    [Fact]
    public void The_class_reads_as_words_for_the_approver()
    {
        Class(DrugSchedule.H1, controlled: true, highAlert: true).Words().ShouldBe("Schedule H1, controlled (NDPS), high-alert");
        Class(DrugSchedule.None).Words().ShouldBe("No schedule, not controlled, not high-alert");
        Class(DrugSchedule.Otc).Words().ShouldBe("Over the counter, not controlled, not high-alert");
    }

    [Fact]
    public void The_approver_is_told_what_handling_the_drug_less_strictly_switches_off()
    {
        // Morphine no longer controlled and no longer Schedule X: out of the register, unwitnessed, sold over the counter.
        DrugClassApprovals.Consequences(Class(DrugSchedule.X, controlled: true), Class(DrugSchedule.None))
            .ShouldBe("Its sales and issues are no longer entered in the statutory register (H1, X or NDPS); " +
                      "a dose given on the ward is no longer witnessed by a second nurse, for orders made from now on; " +
                      "it is sold without a prescription.");

        // Insulin no longer high-alert: only the witness goes.
        DrugClassApprovals.Consequences(Class(DrugSchedule.H, highAlert: true), Class(DrugSchedule.H))
            .ShouldBe("A dose given on the ward is no longer witnessed by a second nurse, for orders made from now on.");

        // A controlled drug no longer high-alert is still witnessed, as a controlled drug: nothing it says goes.
        DrugClassApprovals.Consequences(Class(DrugSchedule.X, controlled: true, highAlert: true), Class(DrugSchedule.X, controlled: true))
            .ShouldBe("It is handled less strictly.");
    }

    [Fact]
    public void Only_a_drug_carries_the_high_alert_flag()
    {
        var item = new Item { Type = ItemType.Consumable, IsHighAlert = true, BaseUnit = "NOS", PurchaseUnit = "NOS", GstRate = 12 };

        var result = item.Validate();

        result.IsFailure.ShouldBeTrue();
        result.Error!.Fields!.Keys.ShouldContain("schedule");
    }
}
