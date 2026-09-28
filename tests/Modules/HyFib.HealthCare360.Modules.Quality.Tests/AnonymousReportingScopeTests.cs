using System.Security.Claims;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

/// <summary>
/// Anonymous incident reports must not be attributable: the platform stamps CreatedBy and writes audit events with the
/// current user, so the report is saved in a scope that cannot see the signed-in user.
/// </summary>
public sealed class AnonymousReportingScopeTests
{
    private static readonly Guid SignedInUser = Guid.NewGuid();

    private static ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void SignIn(IServiceProvider provider)
    {
        var identity = new ClaimsIdentity(
            [new Claim(HcClaims.UserId, SignedInUser.ToString()), new Claim(HcClaims.Session, Guid.NewGuid().ToString())],
            authenticationType: "test");
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    [Fact]
    public async Task Work_in_the_scope_cannot_see_the_signed_in_user()
    {
        await using var provider = BuildServices();
        SignIn(provider);
        using (var outer = provider.CreateScope())
        {
            outer.ServiceProvider.GetRequiredService<ICurrentUser>().UserId.ShouldBe(SignedInUser);
        }

        var scope = new AnonymousReportingScope(provider.GetRequiredService<IServiceScopeFactory>());
        var result = await scope.RunAsync(sp =>
        {
            var user = sp.GetRequiredService<ICurrentUser>();
            return Task.FromResult(Result<(Guid? UserId, bool Authenticated, Guid? Session)>.Ok((user.UserId, user.IsAuthenticated, user.SessionId)));
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value.UserId.ShouldBeNull();
        result.Value.Authenticated.ShouldBeFalse();
        result.Value.Session.ShouldBeNull();
    }

    [Fact]
    public async Task The_callers_own_flow_still_sees_the_user_afterwards()
    {
        await using var provider = BuildServices();
        SignIn(provider);

        await new AnonymousReportingScope(provider.GetRequiredService<IServiceScopeFactory>())
            .RunAsync(_ => Task.FromResult(Result<int>.Ok(1)));

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId.ShouldBe(SignedInUser);
    }

    [Fact]
    public async Task Fails_closed_when_the_user_is_still_visible()
    {
        var leaky = Substitute.For<ICurrentUser>();
        leaky.IsAuthenticated.Returns(true);
        leaky.UserId.Returns(SignedInUser);
        await using var provider = BuildServices(services => services.AddScoped(_ => leaky));
        var ran = false;

        var result = await new AnonymousReportingScope(provider.GetRequiredService<IServiceScopeFactory>()).RunAsync(_ =>
        {
            ran = true;
            return Task.FromResult(Result<int>.Ok(1));
        });

        result.Error!.Code.ShouldBe("incident.anonymity_unavailable");
        ran.ShouldBeFalse();
    }
}
