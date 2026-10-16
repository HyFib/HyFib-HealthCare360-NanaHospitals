using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS SEC-02: access tokens last at most 15 minutes and a session at most 12 hours. A site setting beyond either stops
/// the server at start-up, naming the setting, rather than quietly weakening every sign-in.
/// </summary>
public sealed class PlatformOptionsValidatorTests
{
    [Fact]
    public void The_shipped_defaults_are_within_the_limits() =>
        PlatformOptionsValidator.Problems(new PlatformOptions()).ShouldBeEmpty();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(15, 12)]
    public void Settings_at_or_under_the_limits_are_accepted(int accessTokenMinutes, int sessionHours) =>
        PlatformOptionsValidator.Problems(Options(accessTokenMinutes, sessionHours)).ShouldBeEmpty();

    [Theory]
    [InlineData(16)]
    [InlineData(120)]
    [InlineData(0)]
    public void An_access_token_lifetime_outside_the_limit_is_refused(int minutes) =>
        PlatformOptionsValidator.Problems(Options(accessTokenMinutes: minutes)).ShouldHaveSingleItem()
            .ShouldContain("HealthCare360:Security:AccessTokenMinutes");

    [Theory]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(0)]
    public void A_session_length_outside_the_limit_is_refused(int hours) =>
        PlatformOptionsValidator.Problems(Options(sessionHours: hours)).ShouldHaveSingleItem()
            .ShouldContain("HealthCare360:Security:SessionHours");

    [Fact]
    public void The_options_fail_validation_when_the_server_reads_them()
    {
        using var services = Services(Environments.Production, accessTokenMinutes: 60);

        var error = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<PlatformOptions>>().Value);

        error.Message.ShouldContain("AccessTokenMinutes is 60; it must be between 1 and 15");
    }

    [Fact]
    public void A_development_machine_may_exceed_the_limits()
    {
        using var services = Services(Environments.Development, accessTokenMinutes: 120);

        services.GetRequiredService<IOptions<PlatformOptions>>().Value.Security.AccessTokenMinutes.ShouldBe(120);
    }

    private static PlatformOptions Options(int accessTokenMinutes = 10, int sessionHours = 12) =>
        new() { Security = { AccessTokenMinutes = accessTokenMinutes, SessionHours = sessionHours } };

    private static ServiceProvider Services(string environment, int accessTokenMinutes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        services.AddOptions<PlatformOptions>().Configure(o => o.Security.AccessTokenMinutes = accessTokenMinutes);
        services.AddSingleton<IValidateOptions<PlatformOptions>, PlatformOptionsValidator>();
        return services.BuildServiceProvider();
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "HC360";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
