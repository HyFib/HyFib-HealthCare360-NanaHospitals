using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class IntegrationLogScopeTests
{
    private static readonly Guid Main = Guid.Parse("0f3e2d1c-0000-4000-8000-000000000001");
    private static readonly Guid Branch = Guid.Parse("0f3e2d1c-0000-4000-8000-000000000002");

    [Fact]
    public async Task The_facility_flows_into_awaited_calls_and_ends_with_the_scope()
    {
        IntegrationLogScope.FacilityId.ShouldBeNull();

        using (IntegrationLogScope.ForFacility(Main))
        {
            (await SeenAfterYieldAsync()).ShouldBe(Main);
            (await Task.Run(() => IntegrationLogScope.FacilityId, TestContext.Current.CancellationToken)).ShouldBe(Main);
        }

        IntegrationLogScope.FacilityId.ShouldBeNull();
    }

    [Fact]
    public void An_inner_scope_wins_and_a_null_one_keeps_the_outer_facility()
    {
        using (IntegrationLogScope.ForFacility(Main))
        {
            using (IntegrationLogScope.ForFacility(Branch))
            {
                IntegrationLogScope.FacilityId.ShouldBe(Branch);
            }

            IntegrationLogScope.FacilityId.ShouldBe(Main);
            using (IntegrationLogScope.ForFacility(null))
            {
                IntegrationLogScope.FacilityId.ShouldBe(Main, "a router called without a facility uses the signed-in one, or the caller's");
            }

            IntegrationLogScope.FacilityId.ShouldBe(Main);
        }
    }

    [Fact]
    public async Task Concurrent_calls_keep_their_own_facility()
    {
        async Task<Guid?> CallAsync(Guid facility)
        {
            using var scope = IntegrationLogScope.ForFacility(facility);
            return await SeenAfterYieldAsync();
        }

        var seen = await Task.WhenAll(CallAsync(Main), CallAsync(Branch), CallAsync(Main));

        seen.ShouldBe([Main, Branch, Main]);
    }

    private static async Task<Guid?> SeenAfterYieldAsync()
    {
        await Task.Yield();
        await Task.Delay(5, TestContext.Current.CancellationToken);
        return IntegrationLogScope.FacilityId;
    }
}
