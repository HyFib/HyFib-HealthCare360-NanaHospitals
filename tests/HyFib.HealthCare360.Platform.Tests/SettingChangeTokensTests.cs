using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A saved setting expires every cached value of it (<see cref="SettingChangeTokens"/>), however many readers cached it at
/// the same moment. When the tokens were kept in the memory cache, readers who missed together could each make one; the
/// value cached with a replaced token outlived the next save by up to a minute (seen as an identity check reading the
/// Aadhaar settings of the test before).
/// </summary>
public sealed class SettingChangeTokensTests
{
    private const string Key = "integrations.aadhaar";

    [Fact]
    public void A_change_expires_every_token_readers_took_at_the_same_moment()
    {
        var changes = new SettingChangeTokens();
        var taken = new IChangeToken[256];

        Parallel.For(0, taken.Length, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i => taken[i] = changes.For(Key));
        changes.Changed(Key);

        taken.ShouldAllBe(token => token.HasChanged);
        changes.For(Key).HasChanged.ShouldBeFalse("A reader after the change waits for the next one.");
    }

    [Fact]
    public void A_value_cached_by_readers_who_missed_together_is_gone_after_a_change()
    {
        var changes = new SettingChangeTokens();
        using var cache = new MemoryCache(new MemoryCacheOptions());

        // As SettingsService reads: the token is taken before the value, and the value is cached for a minute. With the
        // tokens in the memory cache, about 2 rounds in 100 left the value cached; 500 rounds find that almost every time.
        for (var round = 0; round < 500; round++)
        {
            var valueKey = $"setting:{Key}:{round}";
            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 32 }, _ =>
                cache.GetOrCreate(valueKey, entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                    entry.AddExpirationToken(changes.For(Key));
                    return "simulator";
                }));

            changes.Changed(Key);

            cache.TryGetValue(valueKey, out _).ShouldBeFalse($"round {round}: the value read before the change is still cached");
        }
    }

    [Fact]
    public void A_change_leaves_other_settings_cached()
    {
        var changes = new SettingChangeTokens();
        var other = changes.For("integrations.sms");
        var token = changes.For(Key);

        changes.Changed(Key);
        changes.Changed("a.setting.nobody.read");

        token.HasChanged.ShouldBeTrue();
        other.HasChanged.ShouldBeFalse();
    }
}
