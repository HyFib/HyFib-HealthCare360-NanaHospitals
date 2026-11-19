using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Messaging;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A paper bill, a receipt from the manual receipt book and a pharmacy sale entered from paper are dated at the time on the
/// paper, so each is in the Tally day book of that day, which may have gone to Tally before the paper was entered: each
/// queues a look at that day in its own transaction. An entry dated today needs none: the nightly post sends it.
/// </summary>
public sealed class LateEntryTests
{
    // 10:00 in India on 25 September 2026.
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 4, 30, 0, TimeSpan.Zero);

    // 22:30 in India on 24 September 2026.
    private static readonly DateTimeOffset LastNight = new(2026, 9, 24, 17, 0, 0, TimeSpan.Zero);

    // 03:30 in India on 25 September 2026, still the evening before in UTC.
    private static readonly DateTimeOffset EarlyToday = new(2026, 9, 24, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_paper_bill_or_receipt_of_an_earlier_day_queues_a_look_at_that_day()
    {
        var outbox = Substitute.For<IOutbox>();
        var queue = new LateEntryQueue(outbox, new FixedTimeProvider(Now));

        queue.Dated(TestData.FacilityId, LastNight, "billing.bill", "NHOB260000042");
        queue.Dated(TestData.FacilityId, LastNight.AddMinutes(5), "billing.receipt", "NHRC260000017");

        outbox.Received(1).Enqueue(
            TallyCorrections.LateEntryMessage, new LateEntry(TestData.FacilityId, new DateOnly(2026, 9, 24), "billing.bill", "NHOB260000042"), TestData.FacilityId);
        outbox.Received(1).Enqueue(
            TallyCorrections.LateEntryMessage, new LateEntry(TestData.FacilityId, new DateOnly(2026, 9, 24), "billing.receipt", "NHRC260000017"), TestData.FacilityId);
    }

    [Fact]
    public void A_paper_entry_dated_today_queues_nothing()
    {
        var outbox = Substitute.For<IOutbox>();
        var queue = new LateEntryQueue(outbox, new FixedTimeProvider(Now));

        queue.Dated(TestData.FacilityId, Now.AddHours(-2), "billing.bill", "NHOB260000043");
        // 03:30 in India is the 25th, though the 24th in UTC.
        queue.Dated(TestData.FacilityId, EarlyToday, "billing.receipt", "NHRC260000018");

        outbox.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pharmacy_sale_dated_in_an_earlier_day_queues_the_same_look()
    {
        var outbox = Substitute.For<IOutbox>();
        var handler = new DayBookEntryBackdatedHandler(new LateEntryQueue(outbox, new FixedTimeProvider(Now)));

        await handler.HandleAsync(new DayBookEntryBackdated(TestData.FacilityId, new DateOnly(2026, 9, 24), "pharmacy.sale", "NHPH260000021"), TestContext.Current.CancellationToken);

        outbox.Received(1).Enqueue(
            TallyCorrections.LateEntryMessage, new LateEntry(TestData.FacilityId, new DateOnly(2026, 9, 24), "pharmacy.sale", "NHPH260000021"), TestData.FacilityId);
    }

    [Fact]
    public async Task A_pharmacy_sale_dated_today_queues_nothing()
    {
        var outbox = Substitute.For<IOutbox>();
        var handler = new DayBookEntryBackdatedHandler(new LateEntryQueue(outbox, new FixedTimeProvider(Now)));

        await handler.HandleAsync(new DayBookEntryBackdated(TestData.FacilityId, new DateOnly(2026, 9, 25), "pharmacy.sale", "NHPH260000022"), TestContext.Current.CancellationToken);

        outbox.ReceivedCalls().ShouldBeEmpty();
    }
}
