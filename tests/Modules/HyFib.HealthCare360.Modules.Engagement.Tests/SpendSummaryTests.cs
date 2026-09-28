using HyFib.HealthCare360.Modules.Engagement.Domain;
using static HyFib.HealthCare360.Modules.Engagement.Services.Patient360Service;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class SpendSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly DateTimeOffset YearAgo = Now.AddYears(-1);

    [Fact]
    public void Cancelled_bills_drop_out_and_refunds_reduce_what_was_paid()
    {
        var kept = Guid.NewGuid();
        var cancelled = Guid.NewGuid();
        SpendRow[] rows =
        [
            new(SpendKind.Billed, kept, 1200m, Now.AddDays(-10)),
            new(SpendKind.Billed, cancelled, 5000m, Now.AddDays(-3)),
            new(SpendKind.BillCancelled, cancelled, 5000m, Now.AddDays(-2)),
            new(SpendKind.Paid, Guid.NewGuid(), 6200m, Now.AddDays(-3)),
            new(SpendKind.Refunded, Guid.NewGuid(), 5000m, Now.AddDays(-1)),
        ];

        var summary = Summarize(rows, YearAgo);

        summary.TotalBilled.ShouldBe(1200m);
        summary.TotalPaid.ShouldBe(1200m);
        summary.BilledLastTwelveMonths.ShouldBe(1200m);
        summary.Bills.ShouldBe(1);
        summary.LastBilledAt.ShouldBe(Now.AddDays(-10));
    }

    [Fact]
    public void Only_bills_of_the_last_twelve_months_count_as_recent()
    {
        SpendRow[] rows =
        [
            new(SpendKind.Billed, Guid.NewGuid(), 800m, Now.AddMonths(-13)),
            new(SpendKind.Billed, Guid.NewGuid(), 300m, Now.AddMonths(-2)),
        ];

        var summary = Summarize(rows, YearAgo);

        summary.TotalBilled.ShouldBe(1100m);
        summary.BilledLastTwelveMonths.ShouldBe(300m);
        summary.Bills.ShouldBe(2);
    }

    [Fact]
    public void A_patient_with_no_bills_has_no_last_bill_date()
    {
        var summary = Summarize([], YearAgo);

        summary.TotalBilled.ShouldBe(0m);
        summary.Bills.ShouldBe(0);
        summary.LastBilledAt.ShouldBeNull();
    }
}
