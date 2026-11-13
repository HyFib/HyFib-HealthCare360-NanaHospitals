using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// What a gateway callback's log row is read from: the request it answers (so it sits beside that call's row) and the
/// error it carries, in either of the gateway's code shapes.
/// </summary>
public sealed class AbdmTraceTests
{
    private static JsonElement Body(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("""{"linkToken":"t","response":{"requestId":"0193-a"}}""", "0193-a")]
    [InlineData("""{"response":{}}""", null)]
    [InlineData("""{"response":"0193-a"}""", null)]
    [InlineData("""{"response":{"requestId":42}}""", null)]
    [InlineData("""{"transactionId":"txn-1"}""", null)]
    [InlineData("""[1,2]""", null)]
    public void A_callback_names_the_request_it_answers_in_response_requestId(string json, string? expected) =>
        AbdmTrace.AnsweredRequestId(Body(json)).ShouldBe(expected);

    [Theory]
    [InlineData("""{"error":{"code":"ABDM-1006","message":"Link token expired"}}""", "ABDM-1006 Link token expired")]
    [InlineData("""{"error":{"code":900901,"message":"Invalid Credentials"}}""", "900901 Invalid Credentials")]
    [InlineData("""{"error":{}}""", "The gateway answered with an error.")]
    [InlineData("""{"error":null,"status":"SUCCESS"}""", null)]
    [InlineData("""{"status":"SUCCESS"}""", null)]
    public void The_error_a_callback_carries_is_read_in_either_shape(string json, string? expected) =>
        AbdmTrace.CarriedError(Body(json)).ShouldBe(expected);

    [Theory]
    [InlineData("sbx", "abdm-sandbox")]
    [InlineData(null, "abdm-sandbox")]
    [InlineData("abdm", "abdm-gateway")]
    public void Calls_to_the_sandbox_and_to_production_are_told_apart(string? cmId, string provider) =>
        AbdmTrace.Provider(cmId).ShouldBe(provider);

    [Fact]
    public void A_status_names_the_gateways_code_when_there_is_one()
    {
        AbdmTrace.Status(202).ShouldBe("HTTP 202");
        AbdmTrace.Status(400, " ABDM-1006 ").ShouldBe("HTTP 400 ABDM-1006");
    }
}
