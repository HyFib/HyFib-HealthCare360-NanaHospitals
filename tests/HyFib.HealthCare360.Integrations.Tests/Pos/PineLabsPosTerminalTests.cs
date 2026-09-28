using System.Net;
using HyFib.HealthCare360.Integrations.Pos;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Pos;

public sealed class PineLabsPosTerminalTests
{
    private static readonly PosSettings Settings = new()
    {
        Enabled = true,
        Provider = "pinelabs",
        MerchantId = "29610",
        UserId = "billing-1",
        Terminals = [new PosTerminalSettings { Id = "OPD-1", Name = "OPD counter", Imei = "CLOUD_TEST_7000", StorePosCode = "1221258270" }],
    };

    private static readonly PosSecrets Secrets = new() { SecurityToken = "test-security-token-0001" };

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PineLabsPosTerminal Terminal => new(_http.Client(), _tracing.Trace);

    [Fact]
    public async Task Uploads_the_billed_amount_to_the_configured_terminal()
    {
        _http.Respond(HttpStatusCode.OK, """{"ResponseCode":0,"ResponseMessage":"APPROVED","PlutusTransactionReferenceID":987654}""");

        var result = await Terminal.StartSaleAsync(new PosSaleRequest("opd-1", 1250.75m, "BILL-42", "upi"), Settings, Secrets, Ct);

        result.TransactionId.ShouldBe("OPD-1:987654:125075");
        result.Status.ShouldBe(PosTransactionStatus.AwaitingCustomer);
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe(new Uri("https://www.plutuscloudserviceuat.in:8201/API/CloudBasedIntegration/V1/UploadBilledTransaction"));
        var body = request.Json();
        body.GetProperty("MerchantID").GetInt32().ShouldBe(29610);
        body.GetProperty("SecurityToken").GetString().ShouldBe("test-security-token-0001");
        body.GetProperty("IMEI").GetString().ShouldBe("CLOUD_TEST_7000");
        body.GetProperty("MerchantStorePosCode").GetString().ShouldBe("1221258270");
        body.GetProperty("TransactionNumber").GetString().ShouldBe("BILL-42");
        body.GetProperty("Amount").GetInt64().ShouldBe(125075);
        body.GetProperty("AllowedPaymentMode").GetString().ShouldBe("10");
        body.GetProperty("AutoCancelDurationInMinutes").GetInt32().ShouldBe(5);
        _tracing.AllText.ShouldNotContain("test-security-token");
    }

    [Fact]
    public async Task Reports_upload_rejections_as_declined()
    {
        _http.Respond(HttpStatusCode.OK, """{"ResponseCode":1,"ResponseMessage":"INVALID MERCHANT"}""");

        var result = await Terminal.StartSaleAsync(new PosSaleRequest("OPD-1", 100m, "BILL-43"), Settings, Secrets, Ct);

        result.Status.ShouldBe(PosTransactionStatus.Declined);
        result.Message.ShouldBe("INVALID MERCHANT");
    }

    [Fact]
    public async Task Declines_unknown_terminals_and_invalid_amounts_without_calling_the_service()
    {
        (await Terminal.StartSaleAsync(new PosSaleRequest("IPD-9", 100m, "BILL-1"), Settings, Secrets, Ct)).Message!.ShouldContain("not configured");
        (await Terminal.StartSaleAsync(new PosSaleRequest("OPD-1", 0.25m, "BILL-1"), Settings, Secrets, Ct)).Status.ShouldBe(PosTransactionStatus.Declined);

        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Polls_status_with_the_reference_carried_in_the_transaction_id()
    {
        _http.Respond(HttpStatusCode.OK, """
            {"ResponseCode":0,"ResponseMessage":"TXN APPROVED","PlutusTransactionReferenceID":987654,
             "TransactionData":[{"Tag":"Approval Code","Value":"00123A"},{"Tag":"Card Number","Value":"************4242"},{"Tag":"RRN","Value":"627318291028"}]}
            """);

        var result = await Terminal.GetStatusAsync("OPD-1:987654:125075", Settings, Secrets, Ct);

        result.ShouldBe(new PosTransactionResult("OPD-1:987654:125075", PosTransactionStatus.Approved, "00123A", "4242", "627318291028", "TXN APPROVED"));
        var body = _http.Requests.ShouldHaveSingleItem().Json();
        body.GetProperty("PlutusTransactionReferenceID").GetInt64().ShouldBe(987654);
    }

    [Fact]
    public async Task A_lost_status_response_keeps_the_counter_waiting()
    {
        _http.Throw(new HttpRequestException("connection reset"));

        var result = await Terminal.GetStatusAsync("OPD-1:987654:125075", Settings, Secrets, Ct);

        result.Status.ShouldBe(PosTransactionStatus.AwaitingCustomer);
        result.TransactionId.ShouldBe("OPD-1:987654:125075");
    }

    [Fact]
    public async Task Cancels_with_the_original_amount()
    {
        _http.Respond(HttpStatusCode.OK, """{"ResponseCode":0,"ResponseMessage":"CANCELLED"}""");

        var result = await Terminal.CancelAsync("OPD-1:987654:125075", Settings, Secrets, Ct);

        result.Status.ShouldBe(PosTransactionStatus.Cancelled);
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Uri.AbsolutePath.ShouldBe("/API/CloudBasedIntegration/V1/CancelTransaction");
        request.Json().GetProperty("Amount").GetInt64().ShouldBe(125075);
    }

    [Theory]
    [InlineData("OPD-1:abc:100")]
    [InlineData("OPD-1:987654")]
    [InlineData("../x:1:1")]
    [InlineData("IPD-9:987654:100")]
    public async Task Unknown_transaction_ids_are_declined_locally(string transactionId)
    {
        (await Terminal.GetStatusAsync(transactionId, Settings, Secrets, Ct)).Status.ShouldBe(PosTransactionStatus.Declined);
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Requires_an_https_service_address()
    {
        var result = await Terminal.StartSaleAsync(new PosSaleRequest("OPD-1", 100m, "BILL-1"), Settings with { BaseUrl = "http://plutus.local" }, Secrets, Ct);

        result.Status.ShouldBe(PosTransactionStatus.Declined);
        _http.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1001, "TXN UPLOADED", PosTransactionStatus.AwaitingCustomer)]
    [InlineData(1, "TRANSACTION CANCELLED BY USER", PosTransactionStatus.Cancelled)]
    [InlineData(1, "TXN TIMED OUT", PosTransactionStatus.TimedOut)]
    [InlineData(1, "CARD DECLINED", PosTransactionStatus.Declined)]
    public void Maps_terminal_response_codes(int code, string message, PosTransactionStatus expected) =>
        PineLabsResponses.MapStatus("OPD-1:1:100", new PineLabsResponse(code, message, 1, new Dictionary<string, string>()), [PineLabsResponses.Pending])
            .Status.ShouldBe(expected);

    [Fact]
    public void Transaction_ids_round_trip()
    {
        var id = PosTransactionIds.Format("OPD-1", 987654, 125075);

        PosTransactionIds.TryParse(id, out var terminal, out var reference, out var paise).ShouldBeTrue();
        (terminal, reference, paise).ShouldBe(("OPD-1", 987654L, 125075L));
    }

    [Theory]
    [InlineData(null, "0")]
    [InlineData("card", "1")]
    [InlineData("UPI", "10")]
    [InlineData("wallet", "0")]
    public void Chooses_the_allowed_payment_mode(string? mode, string expected) =>
        PineLabsPosTerminal.PaymentMode(mode, Settings).ShouldBe(expected);

    [Fact]
    public void Configured_payment_modes_override_the_defaults() =>
        PineLabsPosTerminal.PaymentMode("upi", Settings with { PaymentModes = new Dictionary<string, string> { ["UPI"] = "11" } }).ShouldBe("11");

    [Theory]
    [InlineData("************4242", "4242")]
    [InlineData("4111 1111 1111 1111", "1111")]
    [InlineData("12", null)]
    [InlineData(null, null)]
    public void Keeps_only_the_last_four_card_digits(string? card, string? expected) =>
        PineLabsResponses.Last4(card).ShouldBe(expected);
}
