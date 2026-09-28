using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class RateLimitPoliciesTests
{
    [Fact]
    public void Devices_are_counted_per_credential_not_per_address()
    {
        var first = new DefaultHttpContext();
        first.Request.Headers["X-Device-Key"] = "key-one";
        var second = new DefaultHttpContext();
        second.Request.Headers["X-Device-Key"] = "key-two";

        var a = RateLimitPolicies.DevicePartition(first, "203.0.113.7");
        var b = RateLimitPolicies.DevicePartition(second, "203.0.113.7");

        a.ShouldStartWith("device:");
        a.ShouldNotBe(b);
        a.ShouldNotContain("key-one");
    }

    [Fact]
    public void Biometric_terminals_are_counted_by_serial_number()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?SN=CQUJ123456");

        RateLimitPolicies.DevicePartition(context, "10.0.0.20").ShouldStartWith("device:");
    }

    [Fact]
    public void Requests_without_a_credential_fall_back_to_the_address() =>
        RateLimitPolicies.DevicePartition(new DefaultHttpContext(), "10.0.0.20").ShouldBe("10.0.0.20");
}
