using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A service's price changes only when two people agree: the rule, row by row. What only narrows the terms (no price
/// typed at the counter, no discounts, out of use) is made at once and never waits; a new price or GST rate and every
/// widening wait. While a change waits, a save must fit it: nothing is asked for twice, and any other save is refused. The
/// integration tests drive the same rule over the API; the rows they leave out are here.
/// </summary>
public sealed class ServicePriceTermsTests
{
    private static readonly ServicePriceTerms Open = new(300m, TaxRate: 0m, IsPriceEditable: true, IsDiscountAllowed: true, IsActive: true);
    private static readonly ServicePriceTerms Closed = new(300m, TaxRate: 0m, IsPriceEditable: false, IsDiscountAllowed: false, IsActive: false);

    [Fact]
    public void What_only_narrows_the_terms_is_made_at_once_and_a_new_price_never_is()
    {
        Open.Narrowed(Closed).ShouldBe(Closed, "each of the three narrowed, the price unchanged");
        Open.Narrowed(Closed with { BasePrice = 250m }).ShouldBe(Closed, "a lower price is a new price too: it waits");
        Open.Narrowed(Open with { BasePrice = 350m }).ShouldBe(Open);

        // One at a time: each narrows on its own.
        Open.Narrowed(Open with { IsPriceEditable = false }).ShouldBe(Open with { IsPriceEditable = false });
        Open.Narrowed(Open with { IsDiscountAllowed = false }).ShouldBe(Open with { IsDiscountAllowed = false });
        Open.Narrowed(Open with { IsActive = false }).ShouldBe(Open with { IsActive = false });
    }

    [Fact]
    public void A_new_GST_rate_waits_whichever_way_it_goes()
    {
        // Higher overcharges the patient; lower leaves the hospital owing the tax.
        Open.Narrowed(Open with { TaxRate = 18m }).ShouldBe(Open);
        var taxed = Open with { TaxRate = 18m };
        taxed.Narrowed(Open).ShouldBe(taxed);
        taxed.Narrowed(Closed).ShouldBe(Closed with { TaxRate = 18m }, "the rest narrows, the rate stays");
    }

    [Fact]
    public void Each_widening_waits()
    {
        Closed.Narrowed(Open).ShouldBe(Closed);
        Closed.Narrowed(Closed with { IsPriceEditable = true }).ShouldBe(Closed);
        Closed.Narrowed(Closed with { IsDiscountAllowed = true }).ShouldBe(Closed);
        Closed.Narrowed(Closed with { IsActive = true }).ShouldBe(Closed);
        ServicePriceTerms.None.Narrowed(Open with { TaxRate = 5m }).ShouldBe(ServicePriceTerms.None, "a new service: all of it waits");
    }

    [Fact]
    public void A_save_while_a_change_waits_must_fit_it()
    {
        var now = new ServicePriceTerms(120m, TaxRate: 0m, IsPriceEditable: false, IsDiscountAllowed: true, IsActive: true);
        var waiting = now with { BasePrice = 150m, TaxRate = 5m };

        ServicePriceTerms.Covers(waiting, now, waiting).ShouldBeTrue("saved again as the form shows it");
        ServicePriceTerms.Covers(waiting, now, waiting with { BasePrice = 175m }).ShouldBeFalse("another price");
        ServicePriceTerms.Covers(waiting, now, waiting with { TaxRate = 18m }).ShouldBeFalse("another GST rate");
        ServicePriceTerms.Covers(waiting, now, now).ShouldBeFalse("the price in use put back is another change too");

        // Taken out of use while the price waits: made at once (now narrowed), nothing more to ask.
        var retired = now with { IsActive = false };
        ServicePriceTerms.Covers(waiting, retired, waiting with { IsActive = false }).ShouldBeTrue();

        // Each widening the waiting change does not ask for is asked anew; one it asks for, or already in use, is not.
        ServicePriceTerms.Covers(waiting, now, waiting with { IsPriceEditable = true }).ShouldBeFalse();
        ServicePriceTerms.Covers(waiting with { IsPriceEditable = true }, now, waiting with { IsPriceEditable = true }).ShouldBeTrue();
        ServicePriceTerms.Covers(waiting, now with { IsDiscountAllowed = false }, waiting).ShouldBeTrue("the waiting change allows discounts");
        ServicePriceTerms.Covers(waiting with { IsDiscountAllowed = false }, now with { IsDiscountAllowed = false }, waiting).ShouldBeFalse();
        ServicePriceTerms.Covers(waiting with { IsActive = false }, retired, waiting).ShouldBeFalse("put back in use: not asked yet");
        ServicePriceTerms.Covers(waiting with { IsActive = false }, now, waiting).ShouldBeTrue("in use already");
    }

    [Fact]
    public void The_approver_and_the_audit_trail_read_the_change_in_words()
    {
        var before = new ServicePriceTerms(1234.5m, TaxRate: 0m, IsPriceEditable: false, IsDiscountAllowed: true, IsActive: true);

        ServicePrices.Describe(before, before).ShouldBe("₹1,234.50, unchanged");
        ServicePrices.Describe(before, before with { BasePrice = 120000m, TaxRate = 18m, IsDiscountAllowed = false, IsActive = false })
            .ShouldBe("₹1,234.50 → ₹1,20,000.00; GST exempt → 18%; discounts no longer allowed; taken out of use", "in lakhs, as bills print rupees");
        ServicePrices.Describe(ServicePriceTerms.None, before with { TaxRate = 12.5m, IsPriceEditable = true })
            .ShouldBe("₹0.00 → ₹1,234.50; GST exempt → 12.5%; price typed at the counter allowed; discounts allowed; put in use");
    }
}
