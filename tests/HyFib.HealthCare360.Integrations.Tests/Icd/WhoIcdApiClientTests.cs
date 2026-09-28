using System.Net;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Icd;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;

namespace HyFib.HealthCare360.Integrations.Tests.Icd;

public sealed class WhoIcdApiClientTests
{
    private const string TokenJson = """{"access_token":"eyJ.test.token","expires_in":3600,"token_type":"Bearer","scope":"icdapi_access"}""";

    private static readonly IcdSettings Settings = new() { Enabled = true, Provider = "who" };

    private static readonly IcdSecrets Secrets = new() { ClientId = "client-123", ClientSecret = "secret-456" };

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 25, 5, 0, 0, TimeSpan.Zero));
    private readonly IcdTokenCache _tokens;

    public WhoIcdApiClientTests()
    {
        _tokens = new IcdTokenCache(_clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WhoIcdApiClient Client => new(_http.Client(), _tokens, _tracing.Trace);

    [Fact]
    public async Task Obtains_a_client_credentials_token_then_searches_icd11()
    {
        _http.Respond(HttpStatusCode.OK, TokenJson).Respond(HttpStatusCode.OK, IcdResponseParserTests.SearchJson);

        var results = await Client.SearchIcd11Async("cholera", 10, Settings, Secrets, Ct);

        results.Select(r => r.Code).ShouldBe(["1A00", "1A00.0"]);
        var token = _http.Requests[0];
        token.Method.ShouldBe(HttpMethod.Post);
        token.Uri.ShouldBe(new Uri("https://icdaccessmanagement.who.int/connect/token"));
        token.Body.ShouldBe("client_id=client-123&client_secret=secret-456&scope=icdapi_access&grant_type=client_credentials");
        var search = _http.Requests[1];
        search.Uri.ShouldBe(new Uri("https://id.who.int/icd/release/11/2025-01/mms/search?q=cholera&useFlexisearch=true&flatResults=true&highlightingEnabled=false"));
        search.Header("Authorization").ShouldBe("Bearer eyJ.test.token");
        search.Header("API-Version").ShouldBe("v2");
        search.Header("Accept-Language").ShouldBe("en");
        search.Header("Accept").ShouldBe("application/json");
    }

    [Fact]
    public async Task Never_logs_credentials_or_tokens()
    {
        _http.Respond(HttpStatusCode.OK, TokenJson).Respond(HttpStatusCode.OK, IcdResponseParserTests.SearchJson);

        await Client.SearchIcd11Async("cholera", 10, Settings, Secrets, Ct);

        _tracing.Entries.Select(e => e.Operation).ShouldBe(["token", "icd11.search"]);
        _tracing.AllText.ShouldNotContain("secret-456");
        _tracing.AllText.ShouldNotContain("client-123");
        _tracing.AllText.ShouldNotContain("eyJ.test.token");
    }

    [Fact]
    public async Task Reuses_the_token_until_shortly_before_it_expires()
    {
        _http.Respond(HttpStatusCode.OK, TokenJson)
            .Respond(HttpStatusCode.OK, IcdResponseParserTests.SearchJson)
            .Respond(HttpStatusCode.OK, IcdResponseParserTests.SearchJson)
            .Respond(HttpStatusCode.OK, TokenJson)
            .Respond(HttpStatusCode.OK, IcdResponseParserTests.SearchJson);

        await Client.SearchIcd11Async("cholera", 10, Settings, Secrets, Ct);
        _clock.Advance(TimeSpan.FromMinutes(58));
        await Client.SearchIcd11Async("cholera", 10, Settings, Secrets, Ct);
        _clock.Advance(TimeSpan.FromMinutes(1.5));
        await Client.SearchIcd11Async("cholera", 10, Settings, Secrets, Ct);

        _http.Requests.Count(r => r.Method == HttpMethod.Post).ShouldBe(2);
    }

    [Fact]
    public async Task Looks_up_an_icd10_category_with_its_children()
    {
        _http.Respond(HttpStatusCode.OK, TokenJson)
            .Respond(HttpStatusCode.OK, """{"code":"E11","title":{"@value":"Type 2 diabetes mellitus"},"classKind":"category","child":["http://id.who.int/icd/release/10/2019/E11.0"]}""")
            .Respond(HttpStatusCode.OK, """{"code":"E11.0","title":{"@value":"Type 2 diabetes mellitus with coma"},"classKind":"category"}""");

        var results = await Client.LookupIcd10Async("e11", 10, Settings, Secrets, Ct);

        results.Select(r => (r.Code, r.IsLeaf)).ShouldBe([("E11", false), ("E11.0", true)]);
        results[0].Release.ShouldBe("ICD-10 2019");
        _http.Requests[1].Uri.ShouldBe(new Uri("https://id.who.int/icd/release/10/2019/E11"));
        _http.Requests[2].Uri.ShouldBe(new Uri("https://id.who.int/icd/release/10/2019/E11.0"));
    }

    [Fact]
    public async Task Rejected_credentials_are_reported_without_detail()
    {
        _http.Respond(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""");

        var failure = await Should.ThrowAsync<IntegrationException>(() => Client.SearchIcd11Async("cholera", 10, Settings, Secrets, Ct));

        failure.Message.ShouldBe("The ICD-API rejected the client credentials.");
    }

    [Fact]
    public async Task Api_errors_are_reported_with_the_status_code()
    {
        _http.Respond(HttpStatusCode.OK, TokenJson).Respond(HttpStatusCode.ServiceUnavailable, "down");

        var failure = await Should.ThrowAsync<IntegrationException>(() => Client.SearchIcd11Async("cholera", 10, Settings, Secrets, Ct));

        failure.Message.ShouldBe("ICD-API returned HTTP 503.");
    }

    [Fact]
    public async Task Requires_credentials_and_https_endpoints()
    {
        await Should.ThrowAsync<IntegrationException>(() => Client.SearchIcd11Async("cholera", 10, Settings, new IcdSecrets(), Ct));
        await Should.ThrowAsync<IntegrationException>(() => Client.SearchIcd11Async("cholera", 10, Settings with { ApiBaseUrl = "http://id.who.int" }, Secrets, Ct));
        await Should.ThrowAsync<IntegrationException>(() => Client.SearchIcd11Async("cholera", 10, Settings with { TokenUrl = "http://icdaccessmanagement.who.int/connect/token" }, Secrets, Ct));

        _http.Requests.ShouldBeEmpty();
    }
}
