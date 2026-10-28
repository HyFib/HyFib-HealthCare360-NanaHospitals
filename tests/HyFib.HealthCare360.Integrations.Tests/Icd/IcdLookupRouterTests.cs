using System.Net;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Icd;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Icd;

/// <summary>
/// The router is what Clinical resolves as <c>IIcdLookup</c> (release labels "ICD-11 MMS" / "ICD-10 2019"): inactive
/// until configured, cached, and failures surface as <see cref="HttpRequestException"/> for Clinical's error answer.
/// </summary>
public sealed class IcdLookupRouterTests : IDisposable
{
    private const string TokenJson = """{"access_token":"t","expires_in":3600}""";

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IcdTokenCache _tokens = new(TimeProvider.System);

    public IcdLookupRouterTests()
    {
        _settings.GetAsync<IcdSecrets>("integrations.icd.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new IcdSecrets { ClientId = "client", ClientSecret = "secret" });
        Configure(new IcdSettings { Enabled = true, Provider = "WHO" });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IcdLookupRouter Router => new(
        new IntegrationSettingsReader(_settings, Substitute.For<ICurrentUser>(), Servers.Training, NullLogger<IntegrationSettingsReader>.Instance),
        new WhoIcdApiClient(_http.Client(), _tokens, _tracing.Trace),
        _tracing.Trace,
        _cache,
        NullLogger<IcdLookupRouter>.Instance);

    public void Dispose()
    {
        _cache.Dispose();
        _tokens.Dispose();
    }

    private void Configure(IcdSettings? settings) =>
        _settings.GetAsync<IcdSettings>("integrations.icd", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);

    [Fact]
    public async Task Searches_icd11_online_and_caches_the_answer()
    {
        _http.Respond(HttpStatusCode.OK, TokenJson).Respond(HttpStatusCode.OK, IcdResponseParserTests.SearchJson);

        var first = await Router.SearchAsync("cholera", "ICD-11 MMS", 20, Ct);
        var second = await Router.SearchAsync(" Cholera ", "ICD-11 MMS", 20, Ct);

        first.Select(r => r.Code).ShouldBe(["1A00", "1A00.0"]);
        second.ShouldBe(first);
        _http.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Looks_up_icd10_codes_but_leaves_icd10_text_search_to_the_local_catalogue()
    {
        _http.Respond(HttpStatusCode.OK, TokenJson).Respond(HttpStatusCode.OK, """{"code":"I10","title":{"@value":"Essential (primary) hypertension"}}""");

        (await Router.SearchAsync("hypertension", "ICD-10 2019", 20, Ct)).ShouldBeEmpty();
        var byCode = await Router.SearchAsync("i10", "ICD-10 2019", 20, Ct);

        byCode.ShouldHaveSingleItem().Code.ShouldBe("I10");
        _http.Requests[^1].Uri.AbsolutePath.ShouldBe("/icd/release/10/2019/I10");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("disabled")]
    public async Task Stays_inactive_until_configured(string? provider)
    {
        Configure(provider is null ? null : new IcdSettings { Enabled = true, Provider = provider });

        (await Router.SearchAsync("cholera", "ICD-11 MMS", 20, Ct)).ShouldBeEmpty();

        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_disabled_lookup_returns_nothing()
    {
        Configure(new IcdSettings { Enabled = false, Provider = "who" });

        (await Router.SearchAsync("cholera", "ICD-11 MMS", 20, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_simulator_answers_from_its_training_list_without_calling_who()
    {
        Configure(new IcdSettings { Enabled = true, Provider = "Simulator", Icd11Release = "2025-01" });

        var icd11 = await Router.SearchAsync("type 2 diabetes", "ICD-11 MMS", 20, Ct);
        var byCode = await Router.SearchAsync("CA2", "ICD-11 MMS", 20, Ct);
        var icd10 = await Router.SearchAsync("j45", "ICD-10 2019", 20, Ct);

        icd11.ShouldBe([new IcdSearchResult("5A11", "Type 2 diabetes mellitus", "ICD-11 MMS 2025-01", "05")]);
        byCode.Select(r => r.Code).ShouldBe(["CA22", "CA23"]);
        icd10.Select(r => (r.Code, r.IsLeaf)).ShouldBe([("J45", false), ("J45.9", true)]);
        icd10[0].Release.ShouldBe("ICD-10 2019");
        (await Router.SearchAsync("Z99", "ICD-10 2019", 20, Ct)).ShouldBeEmpty();
        (await Router.SearchAsync("hypertension", "ICD-10 2019", 20, Ct)).ShouldBeEmpty();
        _http.Requests.ShouldBeEmpty();
        _tracing.Entries.Select(e => (e.Integration, e.Provider, e.Operation, e.Success)).ShouldBe(
        [
            ("icd", "simulator", "icd11.search", true),
            ("icd", "simulator", "icd11.search", true),
            ("icd", "simulator", "icd10.lookup", true),
            ("icd", "simulator", "icd10.lookup", true),
        ]);
    }

    [Fact]
    public async Task Failures_surface_as_http_request_exceptions_with_a_staff_facing_message()
    {
        _http.Respond(HttpStatusCode.OK, TokenJson).Respond(HttpStatusCode.ServiceUnavailable, "down");

        var failure = await Should.ThrowAsync<HttpRequestException>(() => Router.SearchAsync("cholera", "ICD-11 MMS", 20, Ct));

        failure.Message.ShouldBe("ICD-API returned HTTP 503.");
        failure.InnerException.ShouldBeOfType<IntegrationException>();
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        _http.Throw(new HttpRequestException("no route to host"))
            .Respond(HttpStatusCode.OK, TokenJson)
            .Respond(HttpStatusCode.OK, IcdResponseParserTests.SearchJson);

        await Should.ThrowAsync<HttpRequestException>(() => Router.SearchAsync("cholera", "ICD-11 MMS", 20, Ct));
        (await Router.SearchAsync("cholera", "ICD-11 MMS", 20, Ct)).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("x")]
    [InlineData("  ")]
    public async Task Ignores_queries_that_are_too_short(string text) =>
        (await Router.SearchAsync(text, "ICD-11 MMS", 20, Ct)).ShouldBeEmpty();

    // IcdRevision is internal, so the theory takes its name (public test methods cannot expose internal types).
    [Theory]
    [InlineData("ICD-10 2019", nameof(IcdRevision.Icd10))]
    [InlineData("ICD-10", nameof(IcdRevision.Icd10))]
    [InlineData("icd10", nameof(IcdRevision.Icd10))]
    [InlineData("10", nameof(IcdRevision.Icd10))]
    [InlineData("ICD-11 MMS", nameof(IcdRevision.Icd11))]
    [InlineData("11", nameof(IcdRevision.Icd11))]
    [InlineData("", nameof(IcdRevision.Icd11))]
    [InlineData(null, nameof(IcdRevision.Icd11))]
    [InlineData("ICD-10-PCS", nameof(IcdRevision.Unsupported))]
    public void Understands_the_release_labels_callers_use(string? release, string expected) =>
        IcdLookupRouter.Revision(release).ShouldBe(Enum.Parse<IcdRevision>(expected));
}
