using System.Text.Json;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Tally;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Tally;

/// <summary>
/// The router is what Billing resolves as <see cref="IAccountingExporter"/>. A facility without Tally must come back as
/// "not configured" (Billing then waits for a file import instead of retrying a failed post every night), while
/// settings that exist but cannot be used must stay a failure that someone sees.
/// </summary>
public sealed class AccountingExporterRouterTests
{
    private const string Key = "integrations.tally";
    private static readonly Guid Facility = Guid.CreateVersion7();
    private static readonly DateOnly Day = new(2026, 9, 25);

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAccountingProvider _tally = Substitute.For<IAccountingProvider>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AccountingExporterRouterTests() => _tally.Key.Returns("tally");

    private AccountingExporterRouter Router =>
        new([_tally], new IntegrationSettingsReader(_settings, Substitute.For<ICurrentUser>(), NullLogger<IntegrationSettingsReader>.Instance), Substitute.For<ICurrentUser>());

    private static AccountingVoucher Voucher(string number) => new(
        "Receipt", Day, number, "Receipt", [new AccountingEntry("Cash", 10m, true), new AccountingEntry("Patient Receivables", 10m, false)], number);

    private void Configure(TallySettings? settings, string? raw = null)
    {
        _settings.GetAsync<TallySettings>(Key, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);
        _settings.GetAsync<JsonElement?>(Key, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(raw is null ? null : JsonDocument.Parse(raw).RootElement.Clone());
    }

    [Fact]
    public async Task A_facility_without_tally_settings_is_not_configured_and_nothing_is_sent()
    {
        Configure(null);

        var result = await Router.PushVouchersAsync([Voucher("RC1")], Facility, Ct);

        result.NotConfigured.ShouldBeTrue();
        result.Success.ShouldBeFalse();
        result.Errors.ShouldBe(0);
        result.Message!.ShouldStartWith("Tally is not set up.");
        result.Vouchers.ShouldHaveSingleItem().Outcome.ShouldBe(AccountingVoucherOutcome.NotSent);
        await _tally.DidNotReceiveWithAnyArgs().PushVouchersAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Switched_off_posting_is_not_configured()
    {
        Configure(new TallySettings { Enabled = false, Provider = "tally" }, """{"enabled":false}""");

        var connection = await Router.GetConnectionAsync(Facility, Ct);

        connection.State.ShouldBe(AccountingConnectionState.NotConfigured);
        connection.Message.ShouldBe("Posting to Tally is switched off in Administration → Integrations.");
    }

    [Fact]
    public async Task Settings_that_cannot_be_read_are_invalid_not_missing()
    {
        _settings.GetAsync<TallySettings>(Key, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns<TallySettings?>(_ => throw new JsonException("bad"));
        _settings.GetAsync<JsonElement?>(Key, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(JsonDocument.Parse("""{"enabled":"yes"}""").RootElement.Clone());

        var result = await Router.PushVouchersAsync([Voucher("RC1")], Facility, Ct);

        result.NotConfigured.ShouldBeFalse();
        result.Errors.ShouldBe(1);
        result.Message.ShouldBe("The Tally settings are not valid. Correct them in Administration → Integrations.");
    }

    [Fact]
    public async Task An_unknown_provider_is_invalid()
    {
        Configure(new TallySettings { Enabled = true, Provider = "busy" }, """{"enabled":true,"provider":"busy"}""");

        var connection = await Router.GetConnectionAsync(Facility, Ct);

        connection.State.ShouldBe(AccountingConnectionState.Invalid);
        connection.Message.ShouldBe("'busy' is not a Tally provider. Choose Tally Prime or the simulator in Administration → Integrations.");
    }

    [Fact]
    public async Task Pushes_with_the_settings_of_the_facility_named()
    {
        var settings = new TallySettings { Enabled = true, Provider = "Tally", CompanyName = "Nana Hospitals - Pune" };
        Configure(settings, "{}");
        _tally.PushVouchersAsync(Arg.Any<IReadOnlyList<AccountingVoucher>>(), settings, Arg.Any<CancellationToken>())
            .Returns(new AccountingExportResult(true, 1, 0, 0, "ok"));

        var result = await Router.PushVouchersAsync([Voucher("RC1")], Facility, Ct);

        result.Success.ShouldBeTrue();
        await _settings.Received().GetAsync<TallySettings>(Key, Facility, Arg.Any<CancellationToken>());
        (await Router.GetConnectionAsync(Facility, Ct)).ShouldBe(new AccountingConnection(AccountingConnectionState.Ready, "tally", null));
    }

    [Fact]
    public async Task Reads_back_through_the_provider_and_compares_with_the_ledger_map()
    {
        var settings = new TallySettings { Enabled = true, Provider = "tally", LedgerMap = new Dictionary<string, string> { ["Cash"] = "Cash-in-Hand" } };
        Configure(settings, "{}");
        _tally.ReadDayAsync(Day, settings, Arg.Any<CancellationToken>()).Returns(new TallyDayRead(true, null,
        [
            new TallyVoucherCopy("HC360-RC1", "Receipt", "RC1", Day, [new TallyLedgerLine("Cash-in-Hand", 10m, true), new TallyLedgerLine("Patient Receivables", 10m, false)], true),
        ]));

        var result = await Router.ReadBackAsync(Day, [Voucher("RC1"), Voucher("RC2")], Facility, Ct);

        result.Success.ShouldBeTrue();
        result.Vouchers.Select(v => (v.Key, v.Match)).ShouldBe([("RC1", AccountingVoucherMatch.Matched), ("RC2", AccountingVoucherMatch.Missing)]);
    }

    [Fact]
    public async Task A_read_back_without_tally_is_not_configured()
    {
        Configure(null);

        var result = await Router.ReadBackAsync(Day, [Voucher("RC1")], Facility, Ct);

        result.Success.ShouldBeFalse();
        result.NotConfigured.ShouldBeTrue();
        result.Vouchers.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_read_is_reported_without_comparing()
    {
        var settings = new TallySettings { Enabled = true, Provider = "tally" };
        Configure(settings, "{}");
        _tally.ReadDayAsync(Day, settings, Arg.Any<CancellationToken>()).Returns(TallyDayRead.Failed("Could not reach Tally."));

        var result = await Router.ReadBackAsync(Day, [Voucher("RC1")], Facility, Ct);

        result.ShouldSatisfyAllConditions(r => r.Success.ShouldBeFalse(), r => r.Message.ShouldBe("Could not reach Tally."), r => r.Vouchers.ShouldBeEmpty());
    }
}
