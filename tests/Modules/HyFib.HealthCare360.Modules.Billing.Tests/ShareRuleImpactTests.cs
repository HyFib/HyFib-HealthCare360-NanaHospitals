using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// What a change of a doctor's sharing rules would share differently (maker-checker, DFA-01): the approver is shown the
/// bill lines it would share otherwise and their shares before and after, worked out by the rule that accrues fees.
/// Taking a doctor's own rule out of use is a change like a raise: the doctor's lines fall back to the hospital's default.
/// </summary>
public sealed class ShareRuleImpactTests
{
    private static readonly Guid Doctor = Guid.NewGuid();
    private static readonly Guid OtherDoctor = Guid.NewGuid();
    private static readonly Guid Suturing = Guid.NewGuid();
    private static readonly Guid Debridement = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 2);

    private static readonly ShareRuleView HospitalDefault =
        new(Guid.NewGuid(), null, null, ServiceCategory.Procedure, null, ShareMethod.Percentage, 30m, new DateOnly(2026, 1, 1), null);

    private static readonly ShareRuleView OwnProcedures =
        new(Guid.NewGuid(), Doctor, null, ServiceCategory.Procedure, null, ShareMethod.Percentage, 20m, new DateOnly(2026, 1, 1), null);

    [Fact]
    public void A_raise_counts_the_lines_it_reaches_with_their_shares_before_and_after()
    {
        ShareLine[] lines = [Line(Doctor, Debridement, 2000m), Line(Doctor, Suturing, 1000m), Line(OtherDoctor, Debridement, 2000m)];
        var raised = OwnProcedures with { Value = 25m };

        var impact = ShareRuleImpact.Of(lines, [HospitalDefault, OwnProcedures], [HospitalDefault, raised]);

        impact.ShouldBe(new ShareImpact(2, 600m, 750m), "the doctor's two lines at 20 % then 25 %; the other doctor's line stays at the default");
        impact.Change.ShouldBe(150m);
    }

    [Fact]
    public void Taking_a_doctors_own_rule_out_of_use_falls_back_to_the_hospitals_default_which_may_share_more()
    {
        ShareLine[] lines = [Line(Doctor, Debridement, 2000m), Line(Doctor, Debridement, 2000m)];

        var impact = ShareRuleImpact.Of(lines, [HospitalDefault, OwnProcedures], [HospitalDefault]);

        impact.ShouldBe(new ShareImpact(2, 800m, 1200m));
    }

    [Fact]
    public void A_more_specific_rule_shares_its_service_and_leaves_the_rest_as_they_were()
    {
        ShareLine[] lines = [Line(Doctor, Suturing, 1000m, quantity: 2m), Line(Doctor, Debridement, 2000m)];
        var suturing = new ShareRuleView(Guid.NewGuid(), Doctor, Suturing, null, null, ShareMethod.FixedPerUnit, 150m, new DateOnly(2026, 1, 1), null);

        var impact = ShareRuleImpact.Of(lines, [HospitalDefault, OwnProcedures], [HospitalDefault, OwnProcedures, suturing]);

        impact.ShouldBe(new ShareImpact(1, 200m, 300m), "₹150 a unit for two sutures instead of 20 % of ₹1,000; the debridement is shared as before");
    }

    [Fact]
    public void A_change_that_shares_no_line_differently_reaches_none()
    {
        ShareLine[] lines = [Line(OtherDoctor, Debridement, 2000m)];

        ShareRuleImpact.Of(lines, [HospitalDefault, OwnProcedures], [HospitalDefault, OwnProcedures with { Value = 50m }])
            .ShouldBe(new ShareImpact(0, 0m, 0m));
    }

    [Fact]
    public void A_new_rule_is_made_out_of_use_with_the_terms_asked_and_its_notes_are_not_terms()
    {
        var asked = new ShareRuleTerms(Doctor, null, ServiceCategory.Procedure, null, ShareMethod.Percentage, 25m, Today, null, true);

        var waiting = asked.OutOfUse();

        waiting.ShouldBe(asked with { IsActive = false });
        var rule = new DoctorShareRule { Notes = "Agreed with the doctor" };
        waiting.ApplyTo(rule);
        ShareRuleTerms.Of(rule).ShouldBe(waiting);
        rule.Notes.ShouldBe("Agreed with the doctor");
        asked.ToView(rule.Id).ShouldBe(new ShareRuleView(rule.Id, Doctor, null, ServiceCategory.Procedure, null, ShareMethod.Percentage, 25m, Today, null));
    }

    private static ShareLine Line(Guid doctor, Guid service, decimal amount, decimal quantity = 1m) =>
        new(doctor, service, ServiceCategory.Procedure, PayerType.Self, quantity, amount, Today);
}
