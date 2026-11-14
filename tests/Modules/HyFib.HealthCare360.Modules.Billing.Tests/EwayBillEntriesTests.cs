using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// An e-way bill generated outside HC360 and entered by hand from its print is checked against what the e-way bill system
/// itself allows (CGST rule 138, GSTN advisories of 17 December 2024 and 4 November 2026).
/// </summary>
public sealed class EwayBillEntriesTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly DateOnly InvoiceDate = new(2026, 11, 10);
    private static readonly DateTimeOffset Generated = new(2026, 11, 12, 10, 15, 0, Ist);
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, Ist);
    private const string Twelve = "331000123456";
    private const string Fourteen = "33100012345678";

    [Fact]
    public void An_e_way_bill_the_portal_could_have_generated_for_the_invoice_has_no_problems() =>
        EwayBillEntries.Problems(Twelve, Generated, Generated.AddDays(2), InvoiceDate, Now).ShouldBeEmpty();

    [Fact]
    public void The_number_is_twelve_or_fourteen_digits_as_printed_spaces_between_groups_allowed()
    {
        EwayBillEntries.Number("3310 0012 3456").ShouldBe(331000123456L);
        EwayBillEntries.Number(Fourteen).ShouldBe(33100012345678L);
        EwayBillEntries.Number("33100012345").ShouldBeNull("11 digits");
        EwayBillEntries.Number("3310001234567").ShouldBeNull("13 digits");
        EwayBillEntries.Number("031000123456").ShouldBeNull("the portal gives the number as a number: no leading zero");
        EwayBillEntries.Number("33100012345A").ShouldBeNull();
        EwayBillEntries.Number("").ShouldBeNull();
        EwayBillEntries.Problems("3310-0012-3456", Generated, null, InvoiceDate, Now)
            .ShouldBe(["The e-way bill number is the portal's 12 digits (14 for one generated from 1 January 2027)."]);
    }

    [Fact]
    public void Fourteen_digit_numbers_are_those_of_e_way_bills_generated_from_1_january_2027()
    {
        EwayBillEntries.Problems(Fourteen, Generated, null, InvoiceDate, Now)
            .ShouldBe(["E-way bill numbers have 14 digits only from 1 January 2027: one generated on 12 Nov 2026 has 12."]);

        var newYear = new DateTimeOffset(2027, 1, 1, 0, 30, 0, Ist);
        var later = newYear.AddDays(1);
        EwayBillEntries.Problems(Fourteen, newYear, null, new DateOnly(2026, 12, 31), later).ShouldBeEmpty();
        EwayBillEntries.Problems(Twelve, newYear, null, new DateOnly(2026, 12, 31), later)
            .ShouldBeEmpty("a 12-digit number already printed, or one the portal still gives, is not refused");
    }

    [Fact]
    public void It_is_generated_on_or_after_the_invoice_date_and_not_in_the_future()
    {
        EwayBillEntries.Problems(Twelve, new DateTimeOffset(2026, 11, 9, 23, 0, 0, Ist), null, InvoiceDate, Now)
            .ShouldBe(["The e-way bill is dated 9 Nov 2026, before the invoice of 10 Nov 2026 it names."]);
        EwayBillEntries.Problems(Twelve, new DateTimeOffset(2026, 11, 10, 0, 5, 0, Ist), null, InvoiceDate, Now).ShouldBeEmpty();
        EwayBillEntries.Problems(Twelve, Now.AddHours(1), null, InvoiceDate, Now).ShouldBe(["The e-way bill is dated in the future."]);
        EwayBillEntries.Problems(Twelve, Now.AddMinutes(5), null, InvoiceDate, Now).ShouldBeEmpty("a clock a few minutes ahead is not refused");
    }

    [Fact]
    public void Since_2025_the_portal_generates_one_only_for_an_invoice_at_most_180_days_old()
    {
        var invoice = new DateOnly(2026, 5, 1);
        var day180 = new DateTimeOffset(invoice.AddDays(180).ToDateTime(new TimeOnly(11, 0)), Ist);
        EwayBillEntries.Problems(Twelve, day180, null, invoice, day180.AddDays(1)).ShouldBeEmpty();
        EwayBillEntries.Problems(Twelve, day180.AddDays(1), null, invoice, day180.AddDays(2))
            .ShouldBe(["The portal generates an e-way bill only for an invoice at most 180 days old: this one is dated 29 Oct 2026, 181 days after the invoice."]);

        // Before 2025 the portal set no such limit.
        var old = new DateOnly(2024, 5, 1);
        var then = new DateTimeOffset(2024, 12, 20, 11, 0, 0, Ist);
        EwayBillEntries.Problems(Twelve, then, null, old, then.AddDays(1)).ShouldBeEmpty();
    }

    [Fact]
    public void Its_validity_ends_after_it_was_generated_and_within_360_days_and_is_blank_while_only_part_a_is_filled()
    {
        EwayBillEntries.Problems(Twelve, Generated, null, InvoiceDate, Now).ShouldBeEmpty("Part-A only: no validity until Part-B");
        EwayBillEntries.Problems(Twelve, Generated, Generated, InvoiceDate, Now)
            .ShouldBe(["The e-way bill must be valid until a time after it was generated."]);
        EwayBillEntries.Problems(Twelve, Generated, Generated.AddHours(-1), InvoiceDate, Now)
            .ShouldBe(["The e-way bill must be valid until a time after it was generated."]);
        EwayBillEntries.Problems(Twelve, Generated, Generated.AddDays(360), InvoiceDate, Now).ShouldBeEmpty();
        EwayBillEntries.Problems(Twelve, Generated, Generated.AddDays(360).AddMinutes(1), InvoiceDate, Now)
            .ShouldBe(["The portal extends an e-way bill to at most 360 days after its generation."]);
    }
}
