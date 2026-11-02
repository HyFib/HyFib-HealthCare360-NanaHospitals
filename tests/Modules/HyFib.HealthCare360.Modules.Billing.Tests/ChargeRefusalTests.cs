using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// An automatic charge billing refused (a bed's day, a consultant's visit), kept until it is charged: counted again on
/// each retry, and named in what System health and a held final bill say, with its day said once and the rest counted.
/// </summary>
public sealed class ChargeRefusalTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 1, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_refusal_is_counted_again_on_each_retry_and_keeps_the_last_reason()
    {
        var refusal = new ChargeRefusal { FirstRefusedAt = Morning };

        refusal.Refused("billing.service_inactive", "Bed class A is no longer offered.", Morning);
        refusal.Refused("patient.merged", new string('x', 400), Morning.AddHours(1));

        (refusal.Attempts, refusal.Code, refusal.LastRefusedAt, refusal.FirstRefusedAt).ShouldBe((2, "patient.merged", Morning.AddHours(1), Morning));
        refusal.Message.Length.ShouldBe(300);
        refusal.Message.ShouldEndWith("…");
    }

    [Fact]
    public void The_refused_charges_are_named_with_their_day_once_and_the_rest_counted()
    {
        ChargeRefusal[] refused =
        [
            Refusal("Bed charges for 01-11-2026", Morning, "Bed class A is no longer offered."),
            Refusal("Consultant visit", Morning.AddDays(1), "Consultant visit (ICU) is no longer offered."),
            Refusal("Diet charges", null, "Renal diet is no longer offered."),
            Refusal("Nursing charges for 03-11-2026", Morning.AddDays(2), "Nursing is no longer offered."),
        ];

        ChargeRefusals.Describe(refused).ShouldBe(
            "Bed charges for 01-11-2026 (Bed class A is no longer offered.); "
            + "Consultant visit on 02-11-2026 (Consultant visit (ICU) is no longer offered.); "
            + "Diet charges (Renal diet is no longer offered.); and 1 more");
    }

    [Fact]
    public void A_final_bill_is_held_while_a_charge_of_its_stay_stands_refused()
    {
        var held = ChargeRefusals.FinalBillHeld([Refusal("Bed charges for 01-11-2026", Morning, "Bed class A is no longer offered.")]);

        held.Code.ShouldBe(ChargeRefusals.RefusedCode);
        held.Message.ShouldStartWith("Billing refused 1 automatic charge(s) of this stay, which would go unbilled: Bed charges for 01-11-2026 (Bed class A is no longer offered.).");
    }

    private static ChargeRefusal Refusal(string description, DateTimeOffset? serviceDate, string reason)
    {
        var refusal = new ChargeRefusal { Description = description, ServiceDate = serviceDate, FirstRefusedAt = Morning };
        refusal.Refused("billing.service_inactive", reason, Morning);
        return refusal;
    }
}
