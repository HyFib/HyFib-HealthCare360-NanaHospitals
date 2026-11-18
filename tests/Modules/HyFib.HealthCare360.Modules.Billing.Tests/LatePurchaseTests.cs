using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Messaging;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A goods receipt posted into stock after the day its goods came: its purchase is in that day's Tally day book, which may
/// have gone to Tally already, so Billing queues a look at that day in Supply chain's transaction. A receipt of today
/// needs none: the nightly post sends today's day book with it.
/// </summary>
public sealed class LatePurchaseTests
{
    // 10:00 in India on 25 September 2026.
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_receipt_of_an_earlier_day_queues_a_look_at_that_day()
    {
        var outbox = Substitute.For<IOutbox>();

        await Handler(outbox).HandleAsync(Posted("NHGRN260000007", new DateOnly(2026, 9, 24)), TestContext.Current.CancellationToken);

        outbox.Received(1).Enqueue(
            TallyCorrections.LatePurchaseMessage, new LatePurchase(TestData.FacilityId, new DateOnly(2026, 9, 24), "NHGRN260000007"), TestData.FacilityId);
    }

    [Fact]
    public async Task A_receipt_of_today_queues_nothing()
    {
        var outbox = Substitute.For<IOutbox>();

        await Handler(outbox).HandleAsync(Posted("NHGRN260000008", new DateOnly(2026, 9, 25)), TestContext.Current.CancellationToken);

        outbox.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Just_after_midnight_in_India_the_day_before_is_an_earlier_day()
    {
        // 00:10 in India is still the evening before in UTC.
        var outbox = Substitute.For<IOutbox>();

        await new GoodsReceiptPostedHandler(outbox, new FixedTimeProvider(new DateTimeOffset(2026, 9, 24, 18, 40, 0, TimeSpan.Zero)))
            .HandleAsync(Posted("NHGRN260000009", new DateOnly(2026, 9, 24)), TestContext.Current.CancellationToken);

        outbox.Received(1).Enqueue(
            TallyCorrections.LatePurchaseMessage, new LatePurchase(TestData.FacilityId, new DateOnly(2026, 9, 24), "NHGRN260000009"), TestData.FacilityId);
    }

    private static GoodsReceiptPostedHandler Handler(IOutbox outbox) => new(outbox, new FixedTimeProvider(Now));

    private static GoodsReceiptPosted Posted(string number, DateOnly receivedOn) => new(Guid.CreateVersion7(), number, TestData.FacilityId, receivedOn, Now);
}
