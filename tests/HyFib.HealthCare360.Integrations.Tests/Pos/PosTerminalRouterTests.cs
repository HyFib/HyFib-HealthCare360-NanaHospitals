using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Pos;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Pos;

/// <summary>
/// The router is what Billing resolves as <see cref="IPosTerminal"/>. Billing stores every attempt keyed by
/// (provider, transaction id), so declined attempts need distinct ids and the provider must be the real one.
/// </summary>
public sealed class PosTerminalRouterTests
{
    private static readonly PosSaleRequest Sale = new("OPD-1", 500m, "0199a1b2c3d4e5f6");

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IPosProvider _pineLabs = Substitute.For<IPosProvider>();

    public PosTerminalRouterTests()
    {
        _pineLabs.Key.Returns("pinelabs");
        _settings.GetAsync<PosSecrets>("integrations.pos.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new PosSecrets());
        Configure(new PosSettings { Enabled = true, Provider = "PineLabs" });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PosTerminalRouter Router() =>
        new([_pineLabs], new IntegrationSettingsReader(_settings, Substitute.For<ICurrentUser>(), NullLogger<IntegrationSettingsReader>.Instance));

    private void Configure(PosSettings? settings) =>
        _settings.GetAsync<PosSettings>("integrations.pos", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);

    [Fact]
    public async Task Stays_inactive_until_configured_and_declines_with_distinct_ids()
    {
        Configure(null);
        var router = Router();

        var first = await router.StartSaleAsync(Sale, Ct);
        var second = await router.StartSaleAsync(Sale, Ct);

        first.Status.ShouldBe(PosTransactionStatus.Declined);
        first.Message.ShouldBe("Card/UPI POS is not configured. Set it up in Administration → Integrations.");
        first.TransactionId.ShouldStartWith("declined-");
        second.TransactionId.ShouldNotBe(first.TransactionId);
        router.Provider.ShouldBe("none");
    }

    [Fact]
    public async Task Reports_the_provider_that_handled_the_sale()
    {
        _pineLabs.StartSaleAsync(default!, default!, default!, Ct)
            .ReturnsForAnyArgs(new PosTransactionResult("OPD-1:987654:50000", PosTransactionStatus.AwaitingCustomer, null, null, null, "Uploaded"));
        var router = Router();

        var result = await router.StartSaleAsync(Sale, Ct);

        result.TransactionId.ShouldBe("OPD-1:987654:50000");
        router.Provider.ShouldBe("pinelabs");
    }

    [Fact]
    public async Task A_sale_the_terminal_never_accepted_still_gets_a_unique_id()
    {
        _pineLabs.StartSaleAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(PosTerminalRouter.Declined(string.Empty, "Terminal 'OPD-9' is not configured."));

        var result = await Router().StartSaleAsync(Sale, Ct);

        result.Status.ShouldBe(PosTransactionStatus.Declined);
        result.TransactionId.ShouldStartWith("declined-");
        result.Message.ShouldBe("Terminal 'OPD-9' is not configured.");
    }

    [Fact]
    public async Task Status_checks_without_configuration_decline_the_same_transaction()
    {
        Configure(new PosSettings { Enabled = false, Provider = "pinelabs" });

        var result = await Router().GetStatusAsync("OPD-1:987654:50000", Ct);

        result.TransactionId.ShouldBe("OPD-1:987654:50000");
        result.Status.ShouldBe(PosTransactionStatus.Declined);
        result.Message.ShouldBe("Card/UPI POS is disabled.");
    }
}
