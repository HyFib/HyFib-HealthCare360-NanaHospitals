using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Sms;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Sms;

public sealed class SmsRouterTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ISmsProvider _msg91 = Substitute.For<ISmsProvider>();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SmsRouterTests()
    {
        _msg91.Key.Returns("msg91");
        _msg91.SendAsync(Arg.Any<SmsMessage>(), Arg.Any<SmsSettings>(), Arg.Any<SmsSecrets>(), Arg.Any<CancellationToken>()).Returns(SendResult.Ok("id-1"));
        _settings.GetAsync<SmsSecrets>("integrations.sms.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new SmsSecrets { Msg91AuthKey = "k" });
    }

    private SmsRouter Router => new([_msg91], new IntegrationSettingsReader(_settings, Substitute.For<ICurrentUser>(), Servers.Live, NullLogger<IntegrationSettingsReader>.Instance), _tracing.Trace);

    private void Configure(SmsSettings? settings) =>
        _settings.GetAsync<SmsSettings>("integrations.sms", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);

    [Fact]
    public async Task Routes_to_the_configured_provider_with_a_normalised_number_and_default_sender()
    {
        Configure(new SmsSettings { Enabled = true, Provider = "MSG91", SenderId = "NANAHS" });

        var result = await Router.SendAsync(new SmsMessage("98765 43210", "Hello", "1107160000000012345"), Ct);

        result.ShouldBe(SendResult.Ok("id-1"));
        await _msg91.Received(1).SendAsync(
            Arg.Is<SmsMessage>(m => m.To == "+919876543210" && m.SenderId == "NANAHS"),
            Arg.Any<SmsSettings>(),
            Arg.Is<SmsSecrets>(s => s.Msg91AuthKey == "k"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Queued_messages_use_the_settings_of_their_facility()
    {
        // Outbox deliveries run with no signed-in user: the facility travels with the message.
        var branch = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000a");
        Configure(null);
        _settings.GetAsync<SmsSettings>("integrations.sms", branch, Arg.Any<CancellationToken>())
            .Returns(new SmsSettings { Enabled = true, Provider = "MSG91", SenderId = "NANABR" });

        var result = await Router.SendAsync(new SmsMessage("+919876543210", "Hello", FacilityId: branch), Ct);

        result.ShouldBe(SendResult.Ok("id-1"));
        await _msg91.Received(1).SendAsync(Arg.Is<SmsMessage>(m => m.SenderId == "NANABR"), Arg.Any<SmsSettings>(), Arg.Any<SmsSecrets>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Queued_messages_are_logged_against_their_facility()
    {
        // Without this, an outbox send (no signed-in user) is logged against no facility and shows under every one.
        var branch = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000b");
        Configure(new SmsSettings { Enabled = true, Provider = "msg91" });
        Guid? seenByProvider = null;
        _msg91.SendAsync(Arg.Any<SmsMessage>(), Arg.Any<SmsSettings>(), Arg.Any<SmsSecrets>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                seenByProvider = IntegrationLogScope.FacilityId;
                return SendResult.Ok("id-2");
            });

        await Router.SendAsync(new SmsMessage("+919876543210", "Hello", FacilityId: branch), Ct);
        await Router.SendAsync(new SmsMessage("not a phone", "Hello", FacilityId: branch), Ct);

        seenByProvider.ShouldBe(branch, "the provider's own log row is written inside the router's scope");
        _tracing.Facilities.ShouldBe([branch], "the router's refusal is logged against the message's facility");
        IntegrationLogScope.FacilityId.ShouldBeNull("the scope ends with the call");
    }

    [Fact]
    public async Task Fails_clearly_when_sms_is_not_configured()
    {
        Configure(null);

        var result = await Router.SendAsync(new SmsMessage("+919876543210", "Hello"), Ct);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not configured");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }

    [Fact]
    public async Task Fails_when_sms_is_disabled()
    {
        Configure(new SmsSettings { Enabled = false, Provider = "msg91" });

        (await Router.SendAsync(new SmsMessage("+919876543210", "Hello"), Ct)).Error.ShouldBe("SMS is disabled.");
    }

    [Fact]
    public async Task Rejects_unknown_providers_and_invalid_numbers()
    {
        Configure(new SmsSettings { Enabled = true, Provider = "carrier-pigeon" });
        (await Router.SendAsync(new SmsMessage("+919876543210", "Hello"), Ct)).Error!.ShouldContain("Unknown SMS provider");

        Configure(new SmsSettings { Enabled = true, Provider = "msg91" });
        (await Router.SendAsync(new SmsMessage("12345", "Hello"), Ct)).Error!.ShouldContain("not a valid phone number");
        await _msg91.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, Ct);
    }
}
