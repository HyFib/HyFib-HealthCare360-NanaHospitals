using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Irp;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Irp;

/// <summary>
/// The NIC e-invoice API v1.03 as HC360 calls it: signing in with the app key under NIC's public key, the session key
/// that comes back under the app key, the invoice under the session key, and the answers read back; the portal's
/// refusals in its own codes, with the IRN it already holds for a duplicate; a GSP's plain JSON; and the simulator,
/// which a live server refuses before any call.
/// </summary>
public sealed class IrpClientTests : IDisposable
{
    private const string Gstin = "33AABCN1234F1Z3";
    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();
    private readonly RSA _nic = RSA.Create(2048);

    // Generated per run: no credential-shaped literal is kept in the tests.
    private readonly IrpSecrets _secrets = new()
    {
        ClientId = Guid.NewGuid().ToString("N"),
        ClientSecret = Guid.NewGuid().ToString("N"),
        UserName = "api_" + Guid.NewGuid().ToString("N")[..8],
        Password = Guid.NewGuid().ToString("N"),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _nic.Dispose();

    private NicIrpClient Client => new(_http.Client(), _tracing.Trace, TimeProvider.System);

    private IrpContext Context(string provider, bool? encrypt = null) => new(
        Guid.NewGuid(), Gstin, new IrpSettings { Enabled = true, Provider = provider, BaseUrl = "https://einv-apisandbox.nic.in", Encrypt = encrypt },
        _secrets with { PublicKeyPem = _nic.ExportSubjectPublicKeyInfoPem() });

    [Fact]
    public void The_payload_round_trips_under_the_session_key()
    {
        var key = NicIrpCrypto.NewAppKey();
        key.Length.ShouldBe(32);
        var data = NicIrpCrypto.EncryptPayload("""{"Version":"1.1"}""", key);
        NicIrpCrypto.DecryptPayload(data, key).ShouldBe("""{"Version":"1.1"}""");
    }

    [Fact]
    public async Task Nic_signs_in_with_the_app_key_under_its_public_key_and_the_invoice_goes_under_the_session_key()
    {
        var sessionKey = RandomNumberGenerator.GetBytes(32);
        byte[]? appKey = null;
        _http.Respond(() =>
        {
            // NIC decrypts the sign-in with its private key, and answers the session key under the app key it found there.
            var sent = JsonNode.Parse(Encoding.UTF8.GetString(_nic.Decrypt(Convert.FromBase64String(_http.Requests[^1].Json().GetProperty("Data").GetString()!),
                RSAEncryptionPadding.Pkcs1)))!;
            sent["UserName"]!.GetValue<string>().ShouldBe(_secrets.UserName);
            appKey = Convert.FromBase64String(sent["AppKey"]!.GetValue<string>());
            return Json(new JsonObject
            {
                ["Status"] = 1,
                ["Data"] = new JsonObject
                {
                    ["AuthToken"] = "token-1",
                    ["Sek"] = Convert.ToBase64String(NicIrpCrypto.Encrypt(sessionKey, appKey)),
                    ["TokenExpiry"] = DateTime.UtcNow.AddHours(11).ToString("yyyy-MM-dd HH:mm:ss"),
                },
            });
        });
        _http.Respond(() =>
        {
            var invoice = NicIrpCrypto.DecryptPayload(_http.Requests[^1].Json().GetProperty("Data").GetString()!, sessionKey);
            JsonNode.Parse(invoice)!["DocDtls"]!["No"]!.GetValue<string>().ShouldBe("NHOB26000001");
            var answer = new JsonObject
            {
                ["AckNo"] = 112610012345678,
                ["AckDt"] = "2026-10-05 11:02:00",
                ["Irn"] = new string('a', 64),
                ["SignedInvoice"] = "x.y.z",
                ["SignedQRCode"] = "a.b.c",
                ["Status"] = "ACT",
            };
            return Json(new JsonObject { ["Status"] = "1", ["Data"] = NicIrpCrypto.EncryptPayload(answer.ToJsonString(), sessionKey) });
        });

        var result = await Client.GenerateAsync(Context("nic"), Invoice(), Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (result.Value!.Irn, result.Value.AckNo, result.Value.SignedQrCode).ShouldBe((new string('a', 64), "112610012345678", "a.b.c"));
        result.Value.AckDate.ShouldBe(new DateTimeOffset(2026, 10, 5, 11, 2, 0, TimeSpan.FromHours(5.5)));
        var call = _http.Requests[^1];
        (call.Uri.AbsolutePath, call.Header("AuthToken"), call.Header("Gstin"), call.Header("user_name"))
            .ShouldBe(("/eicore/v1.03/Invoice", "token-1", Gstin, _secrets.UserName));
        _tracing.Entries.Select(e => e.Operation).ShouldBe(["auth", "irn.generate"]);
        string.Concat(_tracing.Entries.Select(e => e.Request + e.Response + e.Error)).ShouldNotContain(_secrets.Password!);
    }

    [Fact]
    public async Task A_gsp_takes_plain_json_and_a_duplicate_comes_back_with_the_irn_the_portal_holds()
    {
        _http.Respond(HttpStatusCode.OK, """{"Status":1,"Data":{"AuthToken":"gsp-token"}}""");
        var duplicate = new JsonObject
        {
            ["Status"] = 0,
            ["ErrorDetails"] = new JsonArray(new JsonObject { ["ErrorCode"] = "2150", ["ErrorMessage"] = "Duplicate IRN" }),
            ["InfoDtls"] = new JsonArray(new JsonObject
            {
                ["InfCd"] = "DUPIRN",
                ["Desc"] = new JsonObject { ["AckNo"] = 112610012345678, ["AckDt"] = "2026-10-05 11:02:00", ["Irn"] = new string('B', 64) },
            }),
        };
        _http.Respond(HttpStatusCode.OK, duplicate.ToJsonString());

        var result = await Client.GenerateAsync(Context("gsp"), Invoice(), Ct);

        result.Error.ShouldBe(new IrpError("2150", "Duplicate IRN", new string('b', 64)));
        JsonNode.Parse(_http.Requests[0].Body!)!["UserName"]!.GetValue<string>().ShouldBe(_secrets.UserName, "a GSP takes the sign-in as it is");
        JsonNode.Parse(_http.Requests[1].Body!)!["DocDtls"]!["No"]!.GetValue<string>().ShouldBe("NHOB26000001");
    }

    [Fact]
    public async Task An_expired_token_is_asked_for_again_once()
    {
        _http.Respond(HttpStatusCode.OK, """{"Status":1,"Data":{"AuthToken":"old"}}""")
            .Respond(HttpStatusCode.OK, """{"Status":0,"ErrorDetails":[{"ErrorCode":"1005","ErrorMessage":"Invalid Token"}]}""")
            .Respond(HttpStatusCode.OK, """{"Status":1,"Data":{"AuthToken":"new"}}""")
            .Respond(HttpStatusCode.OK, """{"Status":1,"Data":{"Irn":"abc","CancelDate":"2026-10-05 12:00:00"}}""");

        var result = await Client.CancelAsync(Context("gsp"), "abc", 2, "Wrong buyer", Ct);

        result.IsSuccess.ShouldBeTrue();
        _http.Requests[^1].Header("AuthToken").ShouldBe("new");
        JsonNode.Parse(_http.Requests[^1].Body!)!["CnlRsn"]!.GetValue<string>().ShouldBe("2");
    }

    [Fact]
    public async Task The_portals_irn_details_say_whether_the_irn_is_active_or_cancelled()
    {
        var active = new string('a', 64);
        var cancelled = new string('b', 64);
        _http.Respond(HttpStatusCode.OK, """{"Status":1,"Data":{"AuthToken":"gsp-token"}}""")
            .Respond(HttpStatusCode.OK,
                $$$"""{"Status":1,"Data":{"Irn":"{{{active.ToUpperInvariant()}}}","AckNo":"112610012345678","AckDt":"2026-10-05 11:02:00","SignedInvoice":"a.b.c","SignedQRCode":"d.e.f","Status":"ACT"}}""")
            .Respond(HttpStatusCode.OK, $$$"""{"Status":1,"Data":{"Irn":"{{{cancelled}}}","Status":"CNL","CancelDate":"2026-10-05 12:30:00"}}""")
            .Respond(HttpStatusCode.OK, $$$"""{"Status":1,"Data":{"Irn":"{{{active}}}","Status":"ACT"}}""");
        var context = Context("gsp");

        var live = (await Client.GetByIrnAsync(context, active, Ct)).Value.ShouldNotBeNull();
        (live.Irn, live.Status, live.IsCancelled, live.SignedQrCode).ShouldBe((active, "ACT", false, "d.e.f"));
        live.CancelledAt.ShouldBeNull();
        _http.Requests[^1].Uri.AbsolutePath.ShouldBe($"/eicore/v1.03/Invoice/irn/{active}");

        // A cancelled IRN's details are its state: they are read without the acknowledgement or the QR code.
        var gone = (await Client.GetByIrnAsync(context, cancelled, Ct)).Value.ShouldNotBeNull();
        (gone.Irn, gone.IsCancelled).ShouldBe((cancelled, true));
        gone.CancelledAt.ShouldBe(new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.FromHours(5.5)));

        // A live IRN without them is not an answer HC360 can record.
        (await Client.GetByIrnAsync(context, active, Ct)).Error!.Code.ShouldBe(IrpError.Unreachable);
    }

    [Fact]
    public async Task The_simulator_gives_an_irns_details_to_its_own_gstin_and_says_when_it_was_cancelled()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero));
        var simulator = new SimulatorIrp(new SimulatorIrpLedger(), _tracing.Trace, clock);
        var hospital = SimulatorContext(Gstin);
        var irn = (await simulator.GenerateAsync(hospital, Invoice(), Ct)).Value!.Irn;

        (await simulator.GetByIrnAsync(hospital, irn, Ct)).Value!.Status.ShouldBe(IrpIrn.ActiveStatus);
        (await simulator.GetByIrnAsync(SimulatorContext("29AABCN1234F1Z5"), irn, Ct)).IsSuccess.ShouldBeFalse("another GSTIN does not see it");

        clock.Advance(TimeSpan.FromHours(2));
        (await simulator.CancelAsync(hospital, irn, 2, "Wrong buyer", Ct)).IsSuccess.ShouldBeTrue();
        var details = (await simulator.GetByIrnAsync(hospital, irn, Ct)).Value.ShouldNotBeNull();
        (details.IsCancelled, details.CancelledAt).ShouldBe((true, (DateTimeOffset?)clock.Now));
    }

    [Fact]
    public async Task The_simulator_can_lose_its_answer_after_doing_the_work_or_not_be_reached_at_all()
    {
        var ledger = new SimulatorIrpLedger();
        var simulator = new SimulatorIrp(ledger, _tracing.Trace, new TestClock(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero)));
        var hospital = SimulatorContext(Gstin);

        // Not reached: nothing is registered.
        using (ledger.Fail(SimulatorIrpOperations.Generate, SimulatorIrpFault.Down))
        {
            (await simulator.GenerateAsync(hospital, Invoice(), Ct)).Error!.Code.ShouldBe(IrpError.Unreachable);
        }

        ledger.Entries.ShouldBeEmpty();

        // The answer lost: the IRN is registered all the same, and asked for again the document is a duplicate naming it.
        using (ledger.Fail(SimulatorIrpOperations.Generate, SimulatorIrpFault.LoseAnswer))
        {
            (await simulator.GenerateAsync(hospital, Invoice(), Ct)).Error!.Code.ShouldBe(IrpError.Unreachable);
        }

        var irn = ledger.Entries.Keys.ShouldHaveSingleItem();
        (await simulator.GenerateAsync(hospital, Invoice(), Ct)).Error.ShouldBe(new IrpError(IrpError.DuplicateIrn, "Duplicate IRN", irn));

        // A cancellation whose answer is lost is made; a fault is for the calls it was set on, and is cleared with its handle.
        using (ledger.Fail(SimulatorIrpOperations.Cancel, SimulatorIrpFault.LoseAnswer, times: 2))
        {
            (await simulator.CancelAsync(hospital, irn, 2, "Wrong buyer", Ct)).Error!.Code.ShouldBe(IrpError.Unreachable);
        }

        (await simulator.GetByIrnAsync(hospital, irn, Ct)).Value!.IsCancelled.ShouldBeTrue();
        (await simulator.CancelAsync(hospital, irn, 2, "Wrong buyer", Ct)).Error.ShouldBe(new IrpError("9999", "Invoice is not active."));

        // A fault set for one IRN leaves the calls for others alone.
        using (ledger.Fail(SimulatorIrpOperations.Get, SimulatorIrpFault.Down, irn: new string('f', 64)))
        {
            (await simulator.GetByIrnAsync(hospital, irn, Ct)).IsSuccess.ShouldBeTrue();
            (await simulator.GetByIrnAsync(hospital, new string('F', 64), Ct)).Error!.Message.ShouldContain("set to be down");
        }

        _tracing.Entries.Count(e => e.Error?.StartsWith(IrpError.Unreachable, StringComparison.Ordinal) == true).ShouldBe(4, "each failed call is in the log");
    }

    [Fact]
    public async Task Missing_credentials_stop_the_call_before_it_is_sent()
    {
        var context = Context("nic") with { Secrets = new IrpSecrets() };
        var result = await Client.GenerateAsync(context, Invoice(), Ct);
        result.Error!.Code.ShouldBe(IrpError.NotConnected);
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_live_server_refuses_the_simulator_before_any_call()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<IrpSettings>("integrations.irp", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new IrpSettings { Enabled = true, Provider = "simulator" });
        var simulator = new SimulatorIrp(new SimulatorIrpLedger(), _tracing.Trace, TimeProvider.System);

        IrpRouter Router(bool live) => new(
            new IntegrationSettingsReader(settings, Substitute.For<ICurrentUser>(), live ? Servers.Live : Servers.Training,
                NullLogger<IntegrationSettingsReader>.Instance),
            [simulator]);

        var facility = Guid.NewGuid();
        var refused = await Router(live: true).GenerateAsync(facility, Gstin, Invoice(), Ct);
        refused.Error!.Code.ShouldBe(IrpError.NotConnected);
        refused.Error.Message.ShouldContain("only a training or test server can use");
        (await Router(live: true).ConnectionAsync(facility, Ct)).Connected.ShouldBeFalse();

        var training = await Router(live: false).GenerateAsync(facility, Gstin, Invoice(), Ct);
        training.Value!.Irn.ShouldBe(SimulatorIrp.IrnOf(Gstin, new DateOnly(2026, 10, 5), "INV", "NHOB26000001"));
    }

    private static IrpContext SimulatorContext(string gstin) =>
        new(Guid.NewGuid(), gstin, new IrpSettings { Enabled = true, Provider = "simulator" }, new IrpSecrets());

    private static JsonObject Invoice() => new()
    {
        ["Version"] = "1.1",
        ["DocDtls"] = new JsonObject { ["Typ"] = "INV", ["No"] = "NHOB26000001", ["Dt"] = "05/10/2026" },
        ["SellerDtls"] = new JsonObject { ["Gstin"] = Gstin },
        ["BuyerDtls"] = new JsonObject { ["Gstin"] = "32AAACH7409R1ZA" },
        ["ItemList"] = new JsonArray(new JsonObject { ["HsnCd"] = "999799", ["AssAmt"] = 1000m }),
        ["ValDtls"] = new JsonObject { ["TotInvVal"] = 1180m },
    };

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
