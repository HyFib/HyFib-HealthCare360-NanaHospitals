using System.Text.Json;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The feature switches: on until someone turns them off (so an upgrade changes nothing), the hospital-wide switch the
/// master (a facility can only turn a feature off for itself), and anything that is not true or false ignored rather than
/// read as "off".
/// </summary>
public sealed class FeatureFlagsTests
{
    private static readonly Guid FacilityId = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000a");
    private static readonly Guid OtherFacilityId = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000b");

    private readonly FakeSettings _settings = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<bool> IsEnabledAsync(string feature, Guid? facilityId = null) =>
        new FeatureFlags(_settings).IsEnabledAsync(feature, facilityId, Ct);

    [Theory]
    [InlineData(FeatureKeys.PatientPortal)]
    [InlineData(FeatureKeys.Telehealth)]
    public async Task A_feature_nobody_has_switched_is_on(string feature)
    {
        (await IsEnabledAsync(feature)).ShouldBeTrue();
        (await IsEnabledAsync(feature, FacilityId)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public async Task The_hospital_wide_switch_applies_to_every_facility(string value, bool expected)
    {
        _settings.Set(FeatureKeys.Telehealth, value);

        (await IsEnabledAsync(FeatureKeys.Telehealth)).ShouldBe(expected);
        (await IsEnabledAsync(FeatureKeys.Telehealth, FacilityId)).ShouldBe(expected);
    }

    [Fact]
    public async Task A_facility_switch_overrides_the_hospital_wide_one_for_that_facility_only()
    {
        _settings.Set(FeatureKeys.Telehealth, "true");
        _settings.Set(FeatureKeys.Telehealth, "false", FacilityId);

        (await IsEnabledAsync(FeatureKeys.Telehealth, FacilityId)).ShouldBeFalse();
        (await IsEnabledAsync(FeatureKeys.Telehealth, OtherFacilityId)).ShouldBeTrue();
        (await IsEnabledAsync(FeatureKeys.Telehealth)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_facility_cannot_keep_a_feature_on_that_is_off_hospital_wide()
    {
        // Turned off on the Features screen means off everywhere: callers without a facility (patients, guests) read the
        // hospital-wide switch alone, so a branch that stayed on would be refused to them anyway.
        _settings.Set(FeatureKeys.Telehealth, "false");
        _settings.Set(FeatureKeys.Telehealth, "true", FacilityId);

        (await IsEnabledAsync(FeatureKeys.Telehealth, FacilityId)).ShouldBeFalse();
        (await IsEnabledAsync(FeatureKeys.Telehealth, OtherFacilityId)).ShouldBeFalse();
        (await IsEnabledAsync(FeatureKeys.Telehealth)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("\"false\"")]
    [InlineData("\"no\"")]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("{\"enabled\":false}")]
    public async Task A_value_that_is_not_true_or_false_leaves_the_feature_on(string value)
    {
        _settings.Set(FeatureKeys.Telehealth, value);

        (await IsEnabledAsync(FeatureKeys.Telehealth)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"off\"")]
    public async Task A_facility_value_that_is_not_a_switch_leaves_the_hospital_wide_one(string value)
    {
        _settings.Set(FeatureKeys.Telehealth, value, FacilityId);
        (await IsEnabledAsync(FeatureKeys.Telehealth, FacilityId)).ShouldBeTrue();

        _settings.Set(FeatureKeys.Telehealth, "false");
        (await IsEnabledAsync(FeatureKeys.Telehealth, FacilityId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_feature_outside_the_catalogue_is_a_programming_error() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => IsEnabledAsync("features.unknown"));

    [Fact]
    public void Every_feature_is_a_features_key_on_by_default_and_explained()
    {
        FeatureCatalog.All.Select(f => f.Key).ShouldBeUnique();
        foreach (var feature in FeatureCatalog.All)
        {
            feature.Key.ShouldStartWith(FeatureKeys.Prefix);
            feature.Code.ShouldBe(feature.Key[FeatureKeys.Prefix.Length..]);
            feature.DefaultEnabled.ShouldBeTrue($"{feature.Key}: an upgrade must not switch anything off.");
            string[] words = [feature.Name, feature.Description, feature.WhenOff, feature.OffTitle, feature.OffDetailForStaff, feature.OffDetailForPatients];
            words.ShouldAllBe(text => !string.IsNullOrWhiteSpace(text) && !text.Contains('!'));
            FeatureCatalog.Get(feature.Key).ShouldBeSameAs(feature);
        }
    }

    /// <summary>The settings store's merge, in memory: the facility's row when it has one, else the hospital-wide row.</summary>
    private sealed class FakeSettings : ISettingsService
    {
        private readonly Dictionary<(string Key, Guid? FacilityId), string> _rows = [];

        public void Set(string key, string json, Guid? facilityId = null) => _rows[(key, facilityId)] = json;

        public Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default)
        {
            string? json = null;
            if (facilityId is not null)
            {
                _rows.TryGetValue((key, facilityId), out json);
            }

            if (json is null)
            {
                _rows.TryGetValue((key, null), out json);
            }

            return Task.FromResult(json is null ? default : JsonSerializer.Deserialize<T>(json));
        }

        public Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(string key, Guid? facilityId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.Remove((key, facilityId)));
    }
}
