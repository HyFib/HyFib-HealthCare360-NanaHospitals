using System.Security.Authentication;
using HyFib.HealthCare360.Platform.Http;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class OutboundHttpTests
{
    [Fact]
    public void Calls_to_providers_follow_no_redirects_and_need_TLS_1_2_or_later()
    {
        using var handler = OutboundHttp.CreateHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.SslOptions.EnabledSslProtocols.ShouldBe(SslProtocols.Tls12 | SslProtocols.Tls13);
    }
}
