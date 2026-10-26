using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// The ABDM gateway is set up in settings: its status says what is set up and what is missing (never the secret). The
/// refusing client (what the live client falls back to) refuses every call with <c>abdm.gateway_disabled</c>.
/// </summary>
public sealed class AbdmGatewayTests
{
    private const string Complete =
        """{"enabled":true,"baseUrl":"https://dev.abdm.gov.in/api/hiecm","hipId":"IN2710000123","cmId":"sbx","timeoutSeconds":30}""";

    private const string Credentials = """{"clientId":"SBX_000123","clientSecret":"s3cr3t-value"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeAbdmSettings _settings = new();

    private AbdmGatewayConfiguration Configuration => new(_settings);

    [Fact]
    public async Task Nothing_set_up_lists_everything_that_is_missing()
    {
        var status = await Configuration.StatusAsync(Ct);

        status.Enabled.ShouldBeFalse();
        status.Configured.ShouldBeFalse();
        status.Live.ShouldBeFalse();
        status.Problems.Count.ShouldBe(4);
        status.Remaining.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_complete_set_up_switched_on_is_live()
    {
        _settings.Set(AbdmGatewaySettingKeys.Gateway, Complete);
        _settings.Set(AbdmGatewaySettingKeys.Secret, Credentials);

        var status = await Configuration.StatusAsync(Ct);

        status.ShouldSatisfyAllConditions(
            s => s.Enabled.ShouldBeTrue(),
            s => s.Configured.ShouldBeTrue(),
            s => s.Live.ShouldBeTrue(),
            s => s.HasClientId.ShouldBeTrue(),
            s => s.HasClientSecret.ShouldBeTrue(),
            s => s.Problems.ShouldBeEmpty(),
            s => s.HipId.ShouldBe("IN2710000123"));
        JsonSerializer.Serialize(status).ShouldNotContain("s3cr3t-value");
    }

    [Theory]
    [InlineData("""{"enabled":true,"baseUrl":"http://dev.abdm.gov.in/api/hiecm","hipId":"IN27","cmId":"sbx"}""", "https")]
    [InlineData("""{"enabled":true,"baseUrl":"https://user:pw@dev.abdm.gov.in","hipId":"IN27","cmId":"sbx"}""", "https")]
    [InlineData("""{"enabled":true,"baseUrl":"https://dev.abdm.gov.in","cmId":"sbx"}""", "HIP id")]
    [InlineData("""{"enabled":true,"baseUrl":"https://dev.abdm.gov.in","hipId":"IN27","cmId":"ndhm"}""", "consent manager")]
    [InlineData("""{"enabled":"yes"}""", "not valid JSON")]
    public async Task A_wrong_set_up_says_what_is_wrong(string gateway, string problem)
    {
        _settings.Set(AbdmGatewaySettingKeys.Gateway, gateway);
        _settings.Set(AbdmGatewaySettingKeys.Secret, Credentials);

        var status = await Configuration.StatusAsync(Ct);

        status.Configured.ShouldBeFalse();
        status.Problems.ShouldContain(p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_client_refuses_every_call_and_says_why()
    {
        var client = new DisabledAbdmGatewayClient(Configuration);

        var session = await client.CreateSessionAsync(Ct);
        session.IsFailure.ShouldBeTrue();
        session.Error!.Code.ShouldBe(DisabledAbdmGatewayClient.Code);
        session.Error.Message.ShouldContain("switched off");

        _settings.Set(AbdmGatewaySettingKeys.Gateway, Complete);
        _settings.Set(AbdmGatewaySettingKeys.Secret, Credentials);
        var link = await client.LinkCareContextsAsync(
            new AbdmCareContextLink(TestData.PatientId, "Meera K", "meera.k@sbx", AbdmTestData.AbhaNumber, [new AbdmCareContext("x", "Prescription", AbdmRecordType.Prescription)]), "link-token", Guid.CreateVersion7(), Ct);
        link.Error!.Message.ShouldContain("the live client sends this");
        (await client.AnswerConsentNotifyAsync(new AbdmGatewayMessages.OnConsentNotify(new("OK", "c"), new("r"), null), Ct)).IsFailure.ShouldBeTrue();
        (await client.NotifyTransferAsync(new AbdmGatewayMessages.TransferNotify(new("c", "t", "2026-10-26T00:00:00.000Z", new("HIP", "h"), new("FAILED", "h", []))), Ct))
            .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void The_client_secret_is_kept_in_a_secret_setting() => SecretSettingKeys.IsSecret(AbdmGatewaySettingKeys.Secret).ShouldBeTrue();
}
