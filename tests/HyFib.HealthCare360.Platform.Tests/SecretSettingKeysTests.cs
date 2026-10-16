using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class SecretSettingKeysTests
{
    [Theory]
    [InlineData("integrations.sms.secret", true)]
    [InlineData("integrations.payment.secret", true)]
    [InlineData("ai.anthropic.secret", true)]
    [InlineData("ai.speech.SECRET", true)]
    [InlineData("integrations.sms", false)]
    [InlineData("ui.theme", false)]
    [InlineData("integrations.secrets", false)]
    [InlineData("secret.integrations", false)]
    public void Keys_ending_in_secret_hold_credentials(string key, bool secret) =>
        SecretSettingKeys.IsSecret(key).ShouldBe(secret);
}
