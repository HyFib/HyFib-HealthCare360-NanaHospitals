using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// The morning reminder of B2B documents waiting for an IRN: those pending longer than the hospital allows, from the
/// oldest, critical while one can still reach the portal within the week, and naming those past the portal's 30 days.
/// </summary>
public sealed class EInvoiceReminderTests
{
    private static readonly DateOnly Today = new(2026, 11, 13);

    [Fact]
    public void Nothing_is_said_while_no_document_has_waited_longer_than_the_hospital_allows() =>
        EInvoiceReminders.Of([Row("NHOB1", 2), Row("NHOB2", 30, EInvoiceState.Registered), Row("NHOB3", 40, EInvoiceState.NotNeeded),
            Row("NHOB4", 9, EInvoiceState.IrnCancelled)], Today, 2).ShouldBeNull();

    [Fact]
    public void Documents_waiting_too_long_are_named_from_the_oldest_with_a_warning() =>
        EInvoiceReminders.Of([Row("NHOB1", 3), Row("NHOB2", 5), Row("NHOB3", 1)], Today, 2).ShouldBe(new EInvoiceReminder(
            2, "2 e-invoices waiting for an IRN", "2 B2B documents have waited more than 2 days for an IRN; the oldest is NHOB2 of 8 Nov 2026.", "warning"));

    [Fact]
    public void One_the_portal_still_takes_for_a_few_days_makes_it_critical_and_those_past_its_30_days_go_to_the_gst_adviser()
    {
        var reminder = EInvoiceReminders.Of([Row("NHOB1", 26), Row("NHOB2", 24), Row("NHOB3", 35)], Today, 2).ShouldNotBeNull();

        (reminder.Count, reminder.Severity).ShouldBe((3, "critical"));
        reminder.Body.ShouldBe("3 B2B documents have waited more than 2 days for an IRN; the oldest is NHOB3 of 9 Oct 2026. "
            + "2 are due on the portal within 7 days, the first, NHOB1, by 17 Nov 2026. One is past the portal's 30 days: settle it with the hospital's GST adviser.");
    }

    [Fact]
    public void Past_the_portals_30_days_alone_it_is_a_warning() =>
        EInvoiceReminders.Of([Row("NHOB1", 31)], Today, 1).ShouldNotBeNull().ShouldBe(new EInvoiceReminder(1, "1 e-invoice waiting for an IRN",
            "One B2B document has waited more than a day for an IRN: NHOB1 of 13 Oct 2026. It is past the portal's 30 days: settle it with the hospital's GST adviser.",
            "warning"));

    private static EInvoiceRow Row(string number, int daysAgo, EInvoiceState state = EInvoiceState.Pending)
    {
        var date = Today.AddDays(-daysAgo);
        return new EInvoiceRow("bill", Guid.CreateVersion7(), number, date, "32AAACH7409R1ZA", "Malabar Traders Ltd", 1180m, state, date.AddDays(30), false,
            null, null, null);
    }
}
