using System.Net;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// The live gateway client against a fake gateway, checked against NHA's ABDM wrapper (the v3 request classes and
/// <c>application-v3.properties</c>): the session request and its reuse, the headers every call carries, the
/// generate-token and add-care-contexts bodies, both shapes of the gateway's errors, a session the gateway ended, and a
/// gateway that cannot be reached. Nothing is sent while the gateway is switched off.
/// </summary>
public sealed class HttpAbdmGatewayClientTests
{
    private const string Gateway =
        """{"enabled":true,"baseUrl":"https://dev.abdm.gov.in/api/hiecm/","hipId":"IN2710000123","cmId":"sbx","timeoutSeconds":30}""";

    private const string Credentials = """{"clientId":"SBX_000123","clientSecret":"s3cr3t-value"}""";

    private const string Session = """{"accessToken":"session-1","expiresIn":1200,"tokenType":"bearer"}""";

    private static readonly Guid RequestId = Guid.Parse("01923a4b-0000-7000-8000-00000000c001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeAbdmSettings _settings = new();
    private readonly FakeGatewayHandler _gateway = new();
    private readonly AbdmGatewaySessions _sessions = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 26, 9, 30, 15, 123, TimeSpan.Zero));

    public HttpAbdmGatewayClientTests()
    {
        _settings.Set(AbdmGatewaySettingKeys.Gateway, Gateway);
        _settings.Set(AbdmGatewaySettingKeys.Secret, Credentials);
    }

    private HttpAbdmGatewayClient Client()
    {
        var configuration = new AbdmGatewayConfiguration(_settings);
        return new HttpAbdmGatewayClient(
            _gateway.Client(), configuration, new DisabledAbdmGatewayClient(configuration), _sessions, _time, NullLogger<HttpAbdmGatewayClient>.Instance);
    }

    private static AbdmCareContextLink Link() => new(
        TestData.PatientId,
        "Meera K",
        "meera.k@sbx",
        AbdmTestData.AbhaNumber,
        [
            new AbdmCareContext("prescription/1", "Prescription, 12 Oct 2026", AbdmRecordType.Prescription),
            new AbdmCareContext("lab-report/2", "Lab report, 12 Oct 2026", AbdmRecordType.LabReport),
            new AbdmCareContext("radiology-report/3", "Radiology report, 13 Oct 2026", AbdmRecordType.RadiologyReport),
        ]);

    [Fact]
    public async Task A_session_is_started_with_the_client_credentials_and_the_gateway_headers()
    {
        _gateway.Respond(HttpStatusCode.OK, Session);

        var session = await Client().CreateSessionAsync(Ct);

        session.Value.ShouldBe("session-1");
        var sent = _gateway.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.Uri.ToString().ShouldBe("https://dev.abdm.gov.in/api/hiecm/gateway/v3/sessions");
        sent.Header("X-CM-ID").ShouldBe("sbx");
        sent.Header("TIMESTAMP").ShouldBe("2026-10-26T09:30:15.123Z");
        Guid.TryParse(sent.Header("REQUEST-ID"), out _).ShouldBeTrue();
        sent.Header("Authorization").ShouldBeEmpty();
        var body = sent.Json();
        body.GetProperty("clientId").GetString().ShouldBe("SBX_000123");
        body.GetProperty("clientSecret").GetString().ShouldBe("s3cr3t-value");
        body.GetProperty("grantType").GetString().ShouldBe("client_credentials");
    }

    [Fact]
    public async Task The_session_is_reused_until_five_minutes_before_it_expires()
    {
        _gateway.Respond(HttpStatusCode.OK, Session).Respond(HttpStatusCode.Accepted).Respond(HttpStatusCode.Accepted)
            .Respond(HttpStatusCode.OK, """{"accessToken":"session-2","expiresIn":1200}""").Respond(HttpStatusCode.Accepted);
        var client = Client();
        var request = new AbdmLinkTokenRequest("meera.k@sbx", "Meera K", "F", 1984);

        (await client.RequestLinkTokenAsync(request, RequestId, Ct)).IsSuccess.ShouldBeTrue();
        _time.Now += TimeSpan.FromMinutes(14);
        (await client.RequestLinkTokenAsync(request, RequestId, Ct)).IsSuccess.ShouldBeTrue();
        _time.Now += TimeSpan.FromMinutes(2);
        (await client.RequestLinkTokenAsync(request, RequestId, Ct)).IsSuccess.ShouldBeTrue();

        _gateway.Requests.Select(r => r.Uri.AbsolutePath).ShouldBe(
        [
            "/api/hiecm/gateway/v3/sessions",
            "/api/hiecm/v3/token/generate-token",
            "/api/hiecm/v3/token/generate-token",
            "/api/hiecm/gateway/v3/sessions",
            "/api/hiecm/v3/token/generate-token",
        ]);
        _gateway.Requests[2].Header("Authorization").ShouldBe("Bearer session-1");
        _gateway.Requests[4].Header("Authorization").ShouldBe("Bearer session-2");
    }

    [Fact]
    public async Task A_link_token_is_asked_for_with_the_patients_details_and_the_request_id()
    {
        _gateway.Respond(HttpStatusCode.OK, Session).Respond(HttpStatusCode.Accepted);

        var asked = await Client().RequestLinkTokenAsync(new AbdmLinkTokenRequest("meera.k@sbx", "Meera K", "F", 1984), RequestId, Ct);

        asked.IsSuccess.ShouldBeTrue();
        var sent = _gateway.Requests[1];
        sent.Header("REQUEST-ID").ShouldBe(RequestId.ToString("D"));
        sent.Header("X-HIP-ID").ShouldBe("IN2710000123");
        sent.Header("X-CM-ID").ShouldBe("sbx");
        sent.Header("Authorization").ShouldBe("Bearer session-1");
        sent.Header("X-LINK-TOKEN").ShouldBeEmpty();
        sent.Body.ShouldBe("""{"abhaAddress":"meera.k@sbx","name":"Meera K","gender":"F","yearOfBirth":1984}""");
    }

    [Fact]
    public async Task Care_contexts_are_sent_with_the_link_token_one_group_per_health_information_type()
    {
        _gateway.Respond(HttpStatusCode.OK, Session).Respond(HttpStatusCode.Accepted);

        var linked = await Client().LinkCareContextsAsync(Link(), "link-token-1", RequestId, Ct);

        linked.IsSuccess.ShouldBeTrue();
        var sent = _gateway.Requests[1];
        sent.Uri.AbsolutePath.ShouldBe("/api/hiecm/hip/v3/link/carecontext");
        sent.Header("X-LINK-TOKEN").ShouldBe("link-token-1");
        sent.Header("REQUEST-ID").ShouldBe(RequestId.ToString("D"));
        var body = sent.Json();
        body.GetProperty("abhaNumber").GetString().ShouldBe(AbdmTestData.AbhaNumber);
        body.GetProperty("abhaAddress").GetString().ShouldBe("meera.k@sbx");
        var groups = body.GetProperty("patient").EnumerateArray().ToList();
        groups.Select(g => g.GetProperty("hiType").GetString()).ShouldBe(["Prescription", "DiagnosticReport"]);
        groups.ShouldAllBe(g => g.GetProperty("referenceNumber").GetString() == TestData.PatientId.ToString("D"));
        groups.ShouldAllBe(g => g.GetProperty("display").GetString() == "Meera K");
        var reports = groups[1];
        reports.GetProperty("count").GetInt32().ShouldBe(2);
        reports.GetProperty("careContexts").EnumerateArray().Select(c => c.GetProperty("referenceNumber").GetString())
            .ShouldBe(["lab-report/2", "radiology-report/3"]);
        reports.GetProperty("careContexts")[0].GetProperty("display").GetString().ShouldBe("Lab report, 12 Oct 2026");
    }

    [Fact]
    public async Task The_answers_of_user_initiated_linking_go_each_under_a_request_id_of_their_own()
    {
        _gateway.Respond(HttpStatusCode.OK, Session).Respond(HttpStatusCode.Accepted).Respond(HttpStatusCode.Accepted).Respond(HttpStatusCode.Accepted);
        var client = Client();
        var response = new AbdmGatewayMessages.RespRequest("gateway-call-1");

        (await client.AnswerDiscoveryAsync(new AbdmGatewayMessages.OnDiscover("txn-1", [], ["ABHA_ADDRESS"], response, null), Ct)).IsSuccess.ShouldBeTrue();
        (await client.AnswerLinkInitAsync(new AbdmGatewayMessages.OnLinkInit(
            "txn-1", new AbdmGatewayMessages.LinkReference("ref-1", "MEDIATE", new AbdmGatewayMessages.LinkMeta("MOBILE", "+91******3232", "2026-10-26T09:40:15.123Z")), response, null), Ct))
            .IsSuccess.ShouldBeTrue();
        (await client.AnswerLinkConfirmAsync(new AbdmGatewayMessages.OnLinkConfirm(null, response, new("ABDM-1035", "Incorrect OTP")), Ct)).IsSuccess.ShouldBeTrue();

        var answers = _gateway.Requests.Skip(1).ToList();
        answers.Select(r => r.Uri.AbsolutePath).ShouldBe(
        [
            "/api/hiecm/user-initiated-linking/v3/patient/care-context/on-discover",
            "/api/hiecm/user-initiated-linking/v3/link/care-context/on-init",
            "/api/hiecm/user-initiated-linking/v3/link/care-context/on-confirm",
        ]);
        answers.Select(r => r.Header("REQUEST-ID")).Distinct().Count().ShouldBe(3);
        answers.ShouldAllBe(r => r.Header("X-HIP-ID") == "IN2710000123" && r.Json().GetProperty("response").GetProperty("requestId").GetString() == "gateway-call-1");
        answers[1].Json().GetProperty("link").GetProperty("meta").GetProperty("communicationMedium").GetString().ShouldBe("MOBILE");
        answers[2].Body.ShouldBe("""{"response":{"requestId":"gateway-call-1"},"error":{"code":"ABDM-1035","message":"Incorrect OTP"}}""");
    }

    [Theory]
    [InlineData("""{"error":{"code":"ABDM-1006","message":"Invalid link token"}}""", "ABDM-1006 Invalid link token")]
    [InlineData("""{"errors":[{"error":{"code":900901,"message":"Invalid Credentials"}}]}""", "900901 Invalid Credentials")]
    [InlineData("<html>Bad gateway</html>", "HTTP 400")]
    public async Task A_refusal_says_what_the_gateway_said(string answer, string detail)
    {
        _gateway.Respond(HttpStatusCode.OK, Session).Respond(HttpStatusCode.BadRequest, answer);

        var refused = await Client().LinkCareContextsAsync(Link(), "link-token-1", RequestId, Ct);

        refused.Error!.Code.ShouldBe(HttpAbdmGatewayClient.RefusedCode);
        refused.Error.Message.ShouldContain(detail);
        _gateway.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_session_the_gateway_ended_is_renewed_once_and_the_call_sent_again()
    {
        _gateway.Respond(HttpStatusCode.OK, Session).Respond(HttpStatusCode.Unauthorized)
            .Respond(HttpStatusCode.OK, """{"accessToken":"session-2"}""").Respond(HttpStatusCode.Accepted);

        var linked = await Client().LinkCareContextsAsync(Link(), "link-token-1", RequestId, Ct);

        linked.IsSuccess.ShouldBeTrue();
        _gateway.Requests[3].Header("Authorization").ShouldBe("Bearer session-2");
        _gateway.Requests[3].Header("REQUEST-ID").ShouldBe(RequestId.ToString("D"));
    }

    [Fact]
    public async Task A_second_refusal_of_the_session_is_not_retried()
    {
        _gateway.Respond(HttpStatusCode.OK, Session).Respond(HttpStatusCode.Unauthorized)
            .Respond(HttpStatusCode.OK, """{"accessToken":"session-2"}""").Respond(HttpStatusCode.Unauthorized);

        var refused = await Client().LinkCareContextsAsync(Link(), "link-token-1", RequestId, Ct);

        refused.Error!.Code.ShouldBe(HttpAbdmGatewayClient.RefusedCode);
        _gateway.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_session_answer_without_a_token_is_a_refusal()
    {
        _gateway.Respond(HttpStatusCode.OK, """{"tokenType":"bearer"}""");

        var session = await Client().CreateSessionAsync(Ct);

        session.Error!.Code.ShouldBe(HttpAbdmGatewayClient.RefusedCode);
        session.Error.Message.ShouldContain("no access token");
    }

    [Fact]
    public async Task A_gateway_that_cannot_be_reached_changes_nothing_and_is_not_retried()
    {
        _gateway.Respond(HttpStatusCode.OK, Session).Throw(new HttpRequestException("connection refused"));

        var unreachable = await Client().LinkCareContextsAsync(Link(), "link-token-1", RequestId, Ct);

        unreachable.Error!.Code.ShouldBe(HttpAbdmGatewayClient.UnreachableCode);
        _gateway.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Nothing_is_sent_while_the_gateway_is_switched_off()
    {
        _settings.Set(AbdmGatewaySettingKeys.Gateway, Gateway.Replace("\"enabled\":true", "\"enabled\":false", StringComparison.Ordinal));

        var refused = await Client().LinkCareContextsAsync(Link(), "link-token-1", RequestId, Ct);

        refused.Error!.Code.ShouldBe(DisabledAbdmGatewayClient.Code);
        _gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Consent_and_data_flow_answers_go_to_the_wrappers_paths()
    {
        _gateway.Respond(HttpStatusCode.OK, Session).Respond(HttpStatusCode.Accepted).Respond(HttpStatusCode.Accepted).Respond(HttpStatusCode.Accepted);
        var client = Client();
        var response = new AbdmGatewayMessages.RespRequest("gateway-call-2");

        (await client.AnswerConsentNotifyAsync(new AbdmGatewayMessages.OnConsentNotify(new("OK", "consent-1"), response, null), Ct)).IsSuccess.ShouldBeTrue();
        (await client.AnswerHealthInformationRequestAsync(new AbdmGatewayMessages.OnHealthInformationRequest(new("txn-2", "ACKNOWLEDGED"), response, null), Ct))
            .IsSuccess.ShouldBeTrue();
        (await client.NotifyTransferAsync(new AbdmGatewayMessages.TransferNotify(new AbdmGatewayMessages.TransferNotification(
            "consent-1", "txn-2", "2026-10-26T09:30:15.123Z", new("HIP", "IN2710000123"),
            new("TRANSFERRED", "IN2710000123", [new("op-consultation/1", "DELIVERED", "Delivered")]))), Ct)).IsSuccess.ShouldBeTrue();

        var answers = _gateway.Requests.Skip(1).ToList();
        answers.Select(r => r.Uri.AbsolutePath).ShouldBe(
        [
            "/api/hiecm/consent/v3/request/hip/on-notify",
            "/api/hiecm/data-flow/v3/health-information/hip/on-request",
            "/api/hiecm/data-flow/v3/health-information/notify",
        ]);
        answers[0].Body.ShouldBe("""{"acknowledgement":{"status":"OK","consentId":"consent-1"},"response":{"requestId":"gateway-call-2"}}""");
        answers[1].Json().GetProperty("hiRequest").GetProperty("sessionStatus").GetString().ShouldBe("ACKNOWLEDGED");
        var status = answers[2].Json().GetProperty("notification").GetProperty("statusNotification");
        status.GetProperty("sessionStatus").GetString().ShouldBe("TRANSFERRED");
        status.GetProperty("statusResponses")[0].GetProperty("hiStatus").GetString().ShouldBe("DELIVERED");
    }
}
