using System.Security.Claims;
using System.Threading.RateLimiting;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Imaging;

/// <summary>How many thumbnails and images one person can have fetched from PACS at once.</summary>
public sealed class RadiologyImageLimitsTests
{
    private static HttpContext SignedIn(string userId) =>
        new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HcClaims.UserId, userId)], "test")) };

    private static ConcurrencyLimit Defaults()
    {
        var limit = new ConcurrencyLimit();
        RadiologyImageLimits.Defaults(limit);
        return limit;
    }

    [Fact]
    public async Task Each_person_has_a_few_images_in_flight_and_a_queue_behind_them()
    {
        var limit = Defaults();
        var partition = RadiologyImageLimits.Partition(SignedIn("user-1"), limit);
        partition.PartitionKey.ShouldBe("user-1");
        using var limiter = partition.Factory(partition.PartitionKey);

        var running = Enumerable.Range(0, limit.PermitLimit).Select(_ => limiter.AttemptAcquire()).ToList();
        var waiting = Enumerable.Range(0, limit.QueueLimit)
            .Select(_ => limiter.AcquireAsync(1, TestContext.Current.CancellationToken).AsTask()).ToList();
        using var refused = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        running.ShouldAllBe(l => l.IsAcquired);
        waiting.ShouldAllBe(t => !t.IsCompleted);
        refused.IsAcquired.ShouldBeFalse();

        running[0].Dispose();
        (await waiting[0]).IsAcquired.ShouldBeTrue();
        waiting[1].IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void People_do_not_share_a_limit()
    {
        RadiologyImageLimits.Partition(SignedIn("user-1"), Defaults()).PartitionKey
            .ShouldNotBe(RadiologyImageLimits.Partition(SignedIn("user-2"), Defaults()).PartitionKey);
    }

    [Fact]
    public void The_limits_are_the_settings_ones()
    {
        var limit = new ConcurrencyLimit { PermitLimit = 2, QueueLimit = 0 };
        var partition = RadiologyImageLimits.Partition(SignedIn("user-1"), limit);
        using var limiter = partition.Factory(partition.PartitionKey);

        using var first = limiter.AttemptAcquire();
        using var second = limiter.AttemptAcquire();
        using var third = limiter.AttemptAcquire();

        (first.IsAcquired, second.IsAcquired, third.IsAcquired).ShouldBe((true, true, false));
        RadiologyImageLimits.IsValid(new ConcurrencyLimit { PermitLimit = 0, QueueLimit = 4 }).ShouldBeFalse();
    }
}
