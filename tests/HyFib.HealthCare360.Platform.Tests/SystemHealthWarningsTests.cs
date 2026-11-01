using System.Globalization;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-17: System health warns about the server certificate 30 days before it expires, and critically once it is
/// clearly not being issued again; about the hospital CA a year ahead, since replacing it means setting up every device
/// again, and critically in its last 90 days. It names the integrations that are switched on and failing. The integration
/// tests check the same warning over the real API (SystemHealthIntegrationsTests there).
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

    [Theory]
    [InlineData(366, null)]
    [InlineData(364, "warning")]
    [InlineData(91, "warning")]
    [InlineData(89, "critical")]
    [InlineData(29, "critical")]
    public void A_hospital_CA_is_flagged_a_year_before_it_expires_and_critically_in_its_last_90_days(int daysLeft, string? severity)
    {
        var warnings = SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(300), ca: Now.AddDays(daysLeft)), Now);

        if (severity is null)
        {
            warnings.ShouldBeEmpty();
            return;
        }

        var warning = warnings.ShouldHaveSingleItem();
        (warning.Code, warning.Severity).ShouldBe(("cert.ca_expiring", severity));
        warning.Message.ShouldContain(Now.AddDays(daysLeft).ToString("dd MMM yyyy", CultureInfo.InvariantCulture));
        warning.Message.ShouldContain("CA-ROLLOVER.md");
    }

    /// <summary>
    /// After a rollover the devices that still pin the previous CA trust the server only until it expires: a warning for the
    /// whole rollover, so the new CA reaches every tablet, browser and probe, and critical in its last 90 days.
    /// </summary>
    [Theory]
    [InlineData(300, "warning")]
    [InlineData(91, "warning")]
    [InlineData(89, "critical")]
    public void A_rollover_is_flagged_until_the_previous_CA_ends_and_critically_in_its_last_90_days(int daysLeft, string severity)
    {
        var rolled = Certificate(server: Now.AddDays(300), ca: Now.AddYears(10)) with
        {
            PreviousCaFingerprint = "CD34",
            CrossSignedUntil = Now.AddDays(daysLeft),
        };

        var warning = SystemHealthService.CertificateWarnings(rolled, Now).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("cert.rollover_ending", severity));
        warning.Message.ShouldContain(Now.AddDays(daysLeft).ToString("dd MMM yyyy", CultureInfo.InvariantCulture));
        warning.Message.ShouldContain("CA-ROLLOVER.md");
        SystemHealthService.CertificateWarnings(rolled with { PreviousCaFingerprint = null, CrossSignedUntil = null }, Now).ShouldBeEmpty();
    }

    [Fact]
    public void An_expired_hospital_CA_is_critical()
    {
        var warning = SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(300), ca: Now.AddMinutes(-1)), Now).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("cert.ca_expired", "critical"));
    }

    [Fact]
    public void The_server_certificate_warns_30_days_ahead_and_without_a_CA_only_it_is_checked()
    {
        var warning = SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(29), ca: null), Now).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("cert.expiring", "warning"));
        SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(31), ca: Now.AddYears(5)), Now).ShouldBeEmpty();
    }

    [Fact]
    public void A_server_certificate_not_issued_again_two_weeks_before_it_expires_is_critical()
    {
        var issuedHere = SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(13), ca: Now.AddYears(5)), Now).ShouldHaveSingleItem();
        (issuedHere.Code, issuedHere.Severity).ShouldBe(("cert.expiring", "critical"));
        issuedHere.Message.ShouldContain("was not issued again");

        // One an administrator supplied is theirs to replace.
        var supplied = SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(13), ca: null), Now).ShouldHaveSingleItem();
        (supplied.Code, supplied.Severity).ShouldBe(("cert.expiring", "critical"));
        supplied.Message.ShouldContain("Network:CertificatePath");
    }

    [Fact]
    public void A_server_certificate_that_expires_with_the_CA_is_left_to_the_CA()
    {
        // In the CA's last year the server certificate is issued only up to the CA's expiry, so it cannot be renewed past it.
        var warning = SystemHealthService.CertificateWarnings(Certificate(server: Now.AddDays(20), ca: Now.AddDays(20)), Now).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe(("cert.ca_expiring", "critical"));
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
