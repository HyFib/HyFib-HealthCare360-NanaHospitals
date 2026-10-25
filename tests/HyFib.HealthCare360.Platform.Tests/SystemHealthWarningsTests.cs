using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-17: System health warns about the hospital CA as it does about the server certificate, 30 days before it
/// expires, and names the integrations that are switched on and failing. The integration tests check the same warning
/// over the real API (SystemHealthIntegrationsTests there).
/// </summary>
public sealed class SystemHealthWarningsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Administrator_two_step_sign_in_or_desktop_only_administration_switched_off_is_critical()
    {
        SystemHealthService.SecurityControlWarnings(enforceMfa: true, administrationRequiresDesktop: true).ShouldBeEmpty();

        SystemHealthService.SecurityControlWarnings(enforceMfa: false, administrationRequiresDesktop: false)
            .Select(w => (w.Code, w.Severity)).ShouldBe([("security.mfa_off", "critical"), ("security.desktop_admin_off", "critical")]);
    }

    [Fact]
    public void Certificates_far_from_expiry_raise_nothing()
    {
        SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(300), ca: Now.AddYears(9)), Now).ShouldBeEmpty();
    }

    [Fact]
    public void A_hospital_CA_within_30_days_of_expiry_is_a_warning_with_its_date()
    {
        var warning = SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(300), ca: Now.AddDays(29)), Now).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("cert.ca_expiring", "warning"));
        warning.Message.ShouldContain("14 Nov 2026");
    }

    [Fact]
    public void An_expired_hospital_CA_is_critical()
    {
        var warning = SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(300), ca: Now.AddMinutes(-1)), Now).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("cert.ca_expired", "critical"));
    }

    [Fact]
    public void The_server_certificate_warns_in_the_same_window_and_without_a_CA_only_it_is_checked()
    {
        var warning = SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(29), ca: null), Now).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("cert.expiring", "warning"));
        SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(31), ca: Now.AddDays(31)), Now).ShouldBeEmpty();
    }

    [Fact]
    public void Integrations_that_work_are_off_or_not_set_up_raise_nothing()
    {
        SystemHealthService.IntegrationWarnings([
            Integration("SMS", "healthy"),
            Integration("Tally", "degraded"),
            Integration("E-mail", "idle"),
            Integration("PACS", "failing", enabled: false),
            Integration("Card/UPI POS", "not_configured", enabled: false),
        ]).ShouldBeEmpty();
        SystemHealthService.IntegrationWarnings([]).ShouldBeEmpty();
    }

    [Fact]
    public void Every_failing_integration_that_is_switched_on_is_named_in_one_warning()
    {
        var warning = SystemHealthService.IntegrationWarnings([
            Integration("SMS", "failing"),
            Integration("E-mail", "healthy"),
            Integration("Tally", "failing"),
        ]).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("integrations.failing", "warning"));
        warning.Message.ShouldStartWith("SMS, Tally are failing");
        SystemHealthService.IntegrationWarnings([Integration("SMS", "failing")]).ShouldHaveSingleItem().Message.ShouldStartWith("SMS is failing");
    }

    private static CertificateInfo Certificate(DateTimeOffset? server, DateTimeOffset? ca) => new("AB12", server, ["hc360.local"], ca);

    private static IntegrationHealth Integration(string displayName, string health, bool enabled = true) =>
        new(displayName.ToLowerInvariant(), displayName, enabled, health != "not_configured", health, null, null, 0);
}
