using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class LoggedPathTests
{
    private const string Token = "q3W9xLr0bPz7Kd2Nf5Hs8Tv1Yc4Ma6Je";

    [Fact]
    public void A_path_carrying_a_link_token_is_logged_as_its_route_template()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = $"/api/v1/public/unsubscribe/{Token}";
        http.SetEndpoint(Endpoint("/api/v1/public/unsubscribe/{token}", sensitive: true));

        var logged = LoggedPath.Of(http);

        logged.ShouldBe("/api/v1/public/unsubscribe/{token}");
        logged.ShouldNotContain(Token);
    }

    [Fact]
    public void Other_paths_are_logged_as_they_are()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/v1/patients/0199b0c4-4b1c-7c3e-9d2f-2a1b3c4d5e6f";
        http.SetEndpoint(Endpoint("/api/v1/patients/{id:guid}", sensitive: false));

        LoggedPath.Of(http).ShouldBe("/api/v1/patients/0199b0c4-4b1c-7c3e-9d2f-2a1b3c4d5e6f");
    }

    [Fact]
    public void A_failed_request_is_still_withheld_after_the_exception_handler_cleared_the_endpoint()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = $"/portal/feedback/{Token}";
        http.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
        {
            Error = new InvalidOperationException("boom"),
            Endpoint = Endpoint("/portal/{**path}", sensitive: true),
        });

        LoggedPath.Of(http).ShouldBe("/portal/{**path}");
    }

    [Fact]
    public void A_request_without_an_endpoint_is_logged_as_its_path()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/assets/index-3f2a1b.js";

        LoggedPath.Of(http).ShouldBe("/assets/index-3f2a1b.js");
    }

    [Theory]
    [InlineData("portal", AuthService.PortalRefreshCookieName)]
    [InlineData("web", AuthService.RefreshCookieName)]
    [InlineData("desktop", AuthService.RefreshCookieName)]
    public void The_portal_and_the_staff_app_keep_separate_refresh_cookies(string clientType, string cookie) =>
        AuthService.RefreshCookieFor(clientType).ShouldBe(cookie);

    private static RouteEndpoint Endpoint(string template, bool sensitive) =>
        new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template),
            order: 0,
            sensitive ? new EndpointMetadataCollection(SensitivePathMetadata.Instance) : EndpointMetadataCollection.Empty,
            displayName: template);
}
