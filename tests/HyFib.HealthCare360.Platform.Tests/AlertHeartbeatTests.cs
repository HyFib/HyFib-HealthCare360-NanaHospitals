using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The outside heartbeat (audit P2): which addresses it fetches, and when System health says it fails, naming the host but
/// never the rest of the address, which holds the monitoring service's token.
/// </summary>
public sealed class AlertHeartbeatTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("https://hc-ping.com/5f1c2a9e-0d3b-4c41-9f6e-2b7d1e8a4c10")]
    [InlineData("https://uptime.betterstack.com/api/v1/heartbeat/AbCdEf123")]
    [InlineData("http://192.168.1.20:3001/api/push/AbCdEf")]
    [InlineData("http://10.0.0.5/api/push/x")]
    [InlineData("http://172.20.4.9/ping")]
    [InlineData("http://127.0.0.1:8080/ping")]
    [InlineData("http://localhost:8080/ping")]
    [InlineData("http://[fd12:3456:789a::1]/ping")]
    public void An_https_address_or_one_on_the_hospitals_own_network_is_fetched(string address)
    {
        AlertHeartbeat.TryParse(address, out var uri, out var problem).ShouldBeTrue(problem);
        uri.ShouldNotBeNull().AbsoluteUri.ShouldStartWith(address[..address.IndexOf(':', StringComparison.Ordinal)]);
    }

    [Theory]
    [InlineData("http://hc-ping.com/5f1c2a9e", "https address")]
    [InlineData("http://8.8.8.8/ping", "https address")]
    [InlineData("http://172.32.0.1/ping", "https address")]
    [InlineData("https://user:secret@hc-ping.com/abc", "user name or password")]
    [InlineData("ftp://hc-ping.com/abc", "not a web address")]
    [InlineData("hc-ping.com/abc", "not a web address")]
    public void Any_other_address_is_refused_saying_why(string address, string why)
    {
        AlertHeartbeat.TryParse(address, out var uri, out var problem).ShouldBeFalse();
        uri.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldContain(why);
    }

    [Fact]
    public void No_address_is_off_without_a_problem()
    {
        AlertHeartbeat.TryParse("  ", out _, out var problem).ShouldBeFalse();
        problem.ShouldBeNull();
    }

    [Fact]
    public void Two_failures_in_a_row_say_nothing_yet() =>
        AlertHeartbeat.Warning(Failing(AlertHeartbeat.FailuresToWarn - 1)).ShouldBeNull();

    [Fact]
    public void Three_failures_in_a_row_warn_naming_the_host_and_why()
    {
        var warning = AlertHeartbeat.Warning(Failing(AlertHeartbeat.FailuresToWarn)).ShouldNotBeNull();

        (warning.Code, warning.Severity).ShouldBe(("alerts.heartbeat_failing", "warning"));
        warning.Message.ShouldContain("hc-ping.com");
        warning.Message.ShouldContain("29 Oct 2026 09:00 UTC");
        warning.Message.ShouldContain("no answer within 10 seconds.");
        warning.Message.ShouldContain(AlertHeartbeat.UrlSetting);
    }

    [Fact]
    public void A_heartbeat_that_is_off_or_back_says_nothing()
    {
        AlertHeartbeat.Warning(HeartbeatState.Off).ShouldBeNull();
        AlertHeartbeat.Warning(Failing(5) with { Failures = 0, FailingSince = null, LastError = null }).ShouldBeNull();
    }

    private static HeartbeatState Failing(int failures) =>
        new("hc-ping.com", AlertHeartbeat.DefaultMinutes, Since.AddMinutes(10), Since.AddMinutes(-5), failures, Since, "no answer within 10 seconds.");
}
