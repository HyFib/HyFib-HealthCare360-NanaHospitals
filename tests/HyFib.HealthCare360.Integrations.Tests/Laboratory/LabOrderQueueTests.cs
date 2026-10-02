using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>What an order sent unasked contains when its turn comes, and when it is not sent at all.</summary>
public sealed class LabOrderQueueTests
{
    private static readonly InstrumentProfile Profile = new("EM360", "Chemistry", Guid.CreateVersion7(), IsActive: true, IsBidirectional: true, "Astm");

    [Fact]
    public void A_new_sample_sends_everything_still_to_run_now() =>
        LabOrderQueue.ToSend(InstrumentOrderAction.New, ["GLU", "CREA", "UREA"], ["GLU", "UREA"]).ShouldBe(["GLU", "UREA"]);

    [Fact]
    public void A_rerun_sends_only_the_requested_tests_that_are_still_to_run() =>
        LabOrderQueue.ToSend(InstrumentOrderAction.Rerun, ["K", "NA"], ["k", "GLU"]).ShouldBe(["K"]);

    [Fact]
    public void A_cancellation_leaves_out_tests_the_sample_still_needs()
    {
        // Potassium was cancelled from one test but is still ordered through another on the same tube.
        LabOrderQueue.ToSend(InstrumentOrderAction.Cancel, ["K", "TSH"], ["K"]).ShouldBe(["TSH"]);
        LabOrderQueue.ToSend(InstrumentOrderAction.Cancel, ["TSH"], []).ShouldBe(["TSH"]);
    }

    [Fact]
    public void Orders_go_out_only_to_active_bidirectional_analysers_without_the_retired_veto()
    {
        var instrument = new LabInstrumentSettings { Code = "EM360" };

        LabOrderQueue.OffReason(Profile, instrument).ShouldBeNull();
        LabOrderQueue.OffReason(Profile, instrument with { HostQuery = true }).ShouldBeNull();
        LabOrderQueue.OffReason(null, instrument).ShouldNotBeNull().ShouldContain("not set up in Lab setup");
        LabOrderQueue.OffReason(Profile with { IsActive = false }, instrument).ShouldNotBeNull().ShouldContain("not in use");
        LabOrderQueue.OffReason(Profile with { IsBidirectional = false }, instrument).ShouldNotBeNull().ShouldContain("not bidirectional");
        LabOrderQueue.OffReason(Profile, instrument with { HostQuery = false }).ShouldNotBeNull().ShouldContain("hostQuery");
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(5, 480)]
    [InlineData(6, 600)]
    [InlineData(9, 600)]
    public void Refused_orders_wait_longer_each_time(int attempts, int seconds) =>
        LabOrderQueue.RetryDelay(attempts).ShouldBe(TimeSpan.FromSeconds(seconds));
}
