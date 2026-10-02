using System.Text.Json;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Platform.Settings;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

/// <summary>Follow-up settings are read leniently and kept in range, so a typo in Administration never stops the reminders.</summary>
public sealed class EngagementSettingsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static EngagementSettings With(string key, string json)
    {
        using var document = JsonDocument.Parse(json);
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<JsonElement?>(key, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(document.RootElement.Clone()));
        return new EngagementSettings(settings);
    }

    [Fact]
    public async Task Unset_settings_use_the_defaults()
    {
        var settings = new EngagementSettings(Substitute.For<ISettingsService>());

        (await settings.ReminderDaysAsync(null, Ct)).ShouldBe(2);
        (await settings.ChronicReviewMonthsAsync(null, Ct)).ShouldBe(3);
        (await settings.HealthCheckupRecallMonthsAsync(null, Ct)).ShouldBe(12);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("\"7\"", 7)]
    [InlineData("0", 0)]
    [InlineData("90", 14)]
    [InlineData("-3", 0)]
    [InlineData("\"soon\"", 2)]
    [InlineData("true", 2)]
    public async Task Reminder_days_are_a_number_from_zero_to_fourteen(string json, int days) =>
        (await With(EngagementSettings.ReminderDaysKey, json).ReminderDaysAsync(null, Ct)).ShouldBe(days);

    [Theory]
    [InlineData("6", 6)]
    [InlineData("0", 1)]
    [InlineData("24", 12)]
    public async Task Chronic_reviews_are_one_to_twelve_months_apart(string json, int months) =>
        (await With(EngagementSettings.ChronicReviewMonthsKey, json).ChronicReviewMonthsAsync(null, Ct)).ShouldBe(months);

    [Theory]
    [InlineData("24", 24)]
    [InlineData("1", 3)]
    [InlineData("60", 36)]
    public async Task Health_check_ups_recur_every_three_to_thirty_six_months(string json, int months) =>
        (await With(EngagementSettings.HealthCheckupRecallMonthsKey, json).HealthCheckupRecallMonthsAsync(null, Ct)).ShouldBe(months);
}
