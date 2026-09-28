using HyFib.HealthCare360.Integrations.Email;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Email;

public sealed class SmtpEmailProviderTests
{
    private static readonly EmailSettings Valid = new() { Enabled = true, Provider = "smtp", Host = "smtp.example.org", Port = 587, FromAddress = "noreply@example.org" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Accepts_a_complete_starttls_configuration() => SmtpEmailProvider.Validate(Valid).ShouldBeNull();

    [Theory]
    [InlineData(null, 587, "noreply@example.org", "host")]
    [InlineData("smtp.example.org", 465, "noreply@example.org", "587")]
    [InlineData("smtp.example.org", 0, "noreply@example.org", "port")]
    [InlineData("smtp.example.org", 587, "not-an-address", "sender")]
    public void Explains_what_is_wrong_with_the_configuration(string? host, int port, string from, string hint) =>
        SmtpEmailProvider.Validate(Valid with { Host = host, Port = port, FromAddress = from }).ShouldNotBeNull().ShouldContain(hint);

    [Theory]
    [InlineData("ravi.kumar@example.com", "r***@example.com")]
    [InlineData("not-an-address", "***")]
    [InlineData(null, "***")]
    public void Masks_addresses_like_notification_logs(string? address, string expected) =>
        SmtpEmailProvider.MaskAddress(address).ShouldBe(expected);

    [Fact]
    public async Task Rejects_an_invalid_recipient_before_connecting()
    {
        var tracing = new Tracing();

        var result = await new SmtpEmailProvider(tracing.Trace).SendAsync(new EmailMessage("ravi at example", "Hi", "<p>Hi</p>"), Valid, new EmailSecrets(), Ct);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not valid");
        tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }
}
