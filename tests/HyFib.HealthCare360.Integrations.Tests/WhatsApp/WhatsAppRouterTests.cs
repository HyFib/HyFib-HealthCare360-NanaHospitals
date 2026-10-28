using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Integrations.WhatsApp;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.WhatsApp;

public sealed class WhatsAppRouterTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IWhatsAppProvider _meta = Substitute.For<IWhatsAppProvider>();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WhatsAppRouterTests()
    {
        _meta.Key.Returns("meta");
        _meta.SendTextAsync(Arg.Any<WhatsAppTextMessage>(), Arg.Any<WhatsAppSettings>(), Arg.Any<WhatsAppSecrets>(), Arg.Any<CancellationToken>())
            .Returns(SendResult.Ok("wamid.TXT"));
        _settings.GetAsync<WhatsAppSecrets>("integrations.whatsapp.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new WhatsAppSecrets { AccessToken = "t" });
    }

    private WhatsAppRouter Router => new([_meta], new IntegrationSettingsReader(_settings, Substitute.For<ICurrentUser>(), Servers.Live, NullLogger<IntegrationSettingsReader>.Instance), _tracing.Trace);

    private void Configure(WhatsAppSettings? settings) =>
        _settings.GetAsync<WhatsAppSettings>("integrations.whatsapp", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);

    [Fact]
    public async Task Routes_a_text_to_the_configured_provider_with_a_normalised_number()
    {
        Configure(new WhatsAppSettings { Enabled = true, Provider = "Meta", PhoneNumberId = "106540352242922" });

        var result = await Router.SendTextAsync(new WhatsAppTextMessage("98765 43210", "Hello"), Ct);

        result.ShouldBe(SendResult.Ok("wamid.TXT"));
        await _meta.Received(1).SendTextAsync(
            Arg.Is<WhatsAppTextMessage>(m => m.To == "+919876543210" && m.Text == "Hello"),
            Arg.Any<WhatsAppSettings>(),
            Arg.Is<WhatsAppSecrets>(s => s.AccessToken == "t"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_an_empty_text_without_calling_the_provider(string text)
    {
        Configure(new WhatsAppSettings { Enabled = true, Provider = "meta" });

        var result = await Router.SendTextAsync(new WhatsAppTextMessage("+919876543210", text), Ct);

        result.Success.ShouldBeFalse();
        await _meta.DidNotReceiveWithAnyArgs().SendTextAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Refuses_a_text_longer_than_whatsapp_allows()
    {
        Configure(new WhatsAppSettings { Enabled = true, Provider = "meta" });

        var result = await Router.SendTextAsync(new WhatsAppTextMessage("+919876543210", new string('a', WhatsAppRouter.MaxTextLength + 1)), Ct);

        result.Error.ShouldBe("A WhatsApp text must have 1 to 4096 characters.");
    }

    [Fact]
    public async Task Fails_clearly_when_whatsapp_is_not_configured()
    {
        Configure(null);

        var result = await Router.SendTextAsync(new WhatsAppTextMessage("+919876543210", "Hello"), Ct);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not configured");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }
}
