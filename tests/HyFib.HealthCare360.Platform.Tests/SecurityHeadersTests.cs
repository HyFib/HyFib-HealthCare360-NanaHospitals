using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class SecurityHeadersTests
{
    [Fact]
    public async Task Portal_pages_may_open_the_Razorpay_checkout()
    {
        var headers = await HeadersFor("/portal/bills/0199b0c4-4b1c-7c3e-9d2f-2a1b3c4d5e6f");

        var csp = headers.ContentSecurityPolicy.ToString();
        csp.ShouldContain("script-src 'self' https://checkout.razorpay.com https://cdn.razorpay.com;");
        csp.ShouldContain("frame-src 'self' blob: https://checkout.razorpay.com https://api.razorpay.com;");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldContain("connect-src 'self' https://api.razorpay.com https://lumberjack.razorpay.com;");
        headers["Cross-Origin-Opener-Policy"].ToString().ShouldBe("same-origin-allow-popups");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/portal/sign-in")]
    [InlineData("/api/v1/patients")]
    public async Task Pages_send_data_only_to_known_hosts_and_cannot_rebase_their_links(string path)
    {
        var csp = (await HeadersFor(path)).ContentSecurityPolicy.ToString();

        csp.ShouldNotContain(" https:;");
        csp.ShouldNotContain("wss:");
        csp.ShouldContain("base-uri 'none'");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/billing/bills")]
    [InlineData("/api/v1/billing/bills")]
    public async Task The_staff_app_allows_no_third_party_script(string path)
    {
        var headers = await HeadersFor(path);

        headers.ContentSecurityPolicy.ToString().ShouldContain("script-src 'self';");
        headers.ContentSecurityPolicy.ToString().ShouldContain("connect-src 'self';");
        headers.ContentSecurityPolicy.ToString().ShouldNotContain("razorpay");
        headers["Cross-Origin-Opener-Policy"].ToString().ShouldBe("same-origin");
    }

    private static async Task<IHeaderDictionary> HeadersFor(string path)
    {
        var response = new StartingResponseFeature();
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(response);
        http.Request.Path = path;

        await new SecurityHeadersMiddleware(_ => Task.CompletedTask).InvokeAsync(http);
        await response.StartAsync();
        return http.Response.Headers;
    }

    /// <summary>Runs the OnStarting callbacks, as the server does just before it sends the headers.</summary>
    private sealed class StartingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = [];

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));

        public async Task StartAsync()
        {
            foreach (var (callback, state) in _starting)
            {
                await callback(state);
            }
        }
    }
}
