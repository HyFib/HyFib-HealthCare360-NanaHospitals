using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Which changes to a setting wait for a second administrator (<see cref="SettingGuard"/>): switching a control off,
/// raising a limit, clearing a value so the default decides, and any change to where money, patients' data or alerts go.
/// The stricter direction, and every setting that is not guarded, is saved at once. SettingApprovalTests in the
/// integration tests ask for and approve them over the API.
/// </summary>
public sealed class SettingGuardTests
{
    private static readonly JsonNode On = JsonValue.Create(true);
    private static readonly JsonNode Off = JsonValue.Create(false);

    private static bool Waits(string key, JsonNode? inForce, JsonNode? asked) => SettingGuard.Judge(key, inForce, asked, secret: false).Waits;

    private static JsonNode Number(decimal value) => JsonValue.Create(value);

    [Fact]
    public void A_control_switched_on_is_made_at_once_and_switched_off_waits()
    {
        Waits("lab.separateVerifier", Off, On).ShouldBeFalse("a second pathologist asked for is stricter");
        Waits("lab.separateVerifier", null, On).ShouldBeFalse("not set is off for the lab, so on is stricter");
        Waits("lab.separateVerifier", On, Off).ShouldBeTrue("one pathologist alone again");
        Waits("lab.separateVerifier", On, On).ShouldBeFalse("nothing changes");

        // On unless set to false: false while nothing is set switches the second doctor off.
        Waits("mrd.deathCertificateConfirmation", null, Off).ShouldBeTrue();
        Waits("mrd.deathCertificateConfirmation", Off, On).ShouldBeFalse();

        // Typed as text, as the administration rule reads it too.
        Waits("security.administrationRequiresDesktop", On, JsonValue.Create("false")).ShouldBeTrue();
        Waits("security.administrationRequiresDesktop", Off, JsonValue.Create(" TRUE ")).ShouldBeFalse();

        var change = SettingGuard.Judge("lab.separateVerifier", On, Off, secret: false);
        change.Summary.ShouldBe("Second pathologist for lab corrections: on → off");
        SettingGuard.Judge("lab.separateVerifier", null, On, secret: false).From.ShouldBe("not set (off)");
    }

    [Fact]
    public void A_switch_that_lets_something_through_waits_when_switched_on()
    {
        Waits("ui.biometricUnlock", null, On).ShouldBeTrue("a colleague's Windows Hello could unlock the screen");
        Waits("ui.biometricUnlock", On, Off).ShouldBeFalse();
        Waits("ai.enabled", Off, On).ShouldBeTrue("clinical text then leaves the hospital");
        Waits("ai.enabled", On, Off).ShouldBeFalse();
    }

    [Fact]
    public void A_limit_lowered_is_made_at_once_and_raised_waits()
    {
        Waits("billing.discount.defaultMaxPercent", Number(10), Number(5)).ShouldBeFalse();
        Waits("billing.discount.defaultMaxPercent", Number(10), Number(25)).ShouldBeTrue();
        Waits("billing.discount.defaultMaxPercent", null, Number(5)).ShouldBeTrue("not set, nobody may discount on their own authority (0%)");
        Waits("billing.discount.defaultMaxPercent", Number(100), Number(150)).ShouldBeFalse("read as 100% either way");
        Waits("pharmacy.discount.max_percent", null, Number(15)).ShouldBeTrue("above the 10% default");
        Waits("pharmacy.discount.max_percent", null, JsonValue.Create("8")).ShouldBeFalse("a number typed as text is read as one");

        SettingGuard.Judge("billing.discount.defaultMaxPercent", Number(10), Number(25), secret: false).Summary
            .ShouldBe("Own-authority discount limit: 10% → 25%");
    }

    [Fact]
    public void Zero_for_no_limit_is_the_loosest_of_all()
    {
        Waits("security.maxSessionsPerUser", Number(3), Number(0)).ShouldBeTrue("0 is no limit");
        Waits("security.maxSessionsPerUser", Number(0), Number(5)).ShouldBeFalse("a limit where there was none");
        Waits("ui.idleLockMinutes", Number(10), Number(0)).ShouldBeTrue("0 never locks the screen");
        Waits("ui.idleLockMinutes", Number(10), Number(30)).ShouldBeTrue();
        Waits("ui.idleLockMinutes", Number(10), Number(5)).ShouldBeFalse();
        Waits("lab.deltaCheckPercent", null, Number(0)).ShouldBeTrue("0 turns the check off");
        Waits("lab.deltaCheckPercent", Number(50), Number(20)).ShouldBeFalse("flags smaller changes");
        Waits("supportservices.canteen.staffCreditLimit", Number(3000), Number(0)).ShouldBeTrue();

        SettingGuard.Judge("security.maxSessionsPerUser", Number(3), Number(0), secret: false).Summary
            .ShouldBe("Sessions per staff account: 3 sessions → no limit");
    }

    [Fact]
    public void A_grace_or_look_back_is_judged_as_its_module_reads_it()
    {
        Waits("inpatient.marGraceMinutes", Number(30), Number(60)).ShouldBeTrue("a dose shows overdue later");
        Waits("inpatient.marGraceMinutes", Number(30), Number(1000)).ShouldBeFalse("outside 5 to 240 the chart keeps 30 minutes");
        Waits("inpatient.marGraceMinutes", Number(30), Number(30.5m)).ShouldBeTrue("not a whole number: the chart cannot read it");

        // Fewer days back compare fewer results: lowering the look-back loosens the check.
        Waits("lab.deltaCheckWindowDays", Number(30), Number(7)).ShouldBeTrue();
        Waits("lab.deltaCheckWindowDays", Number(30), Number(90)).ShouldBeFalse();
    }

    [Fact]
    public void A_value_cleared_or_unreadable_waits()
    {
        Waits("lab.separateVerifier", On, null).ShouldBeTrue("cleared, the lab's default (off) decides");
        Waits("billing.shift.required", On, null).ShouldBeTrue("cleared, even where the default is on: nobody read which");
        Waits("security.maxSessionsPerUser", Number(3), null).ShouldBeTrue();
        Waits("lab.separateVerifier", null, null).ShouldBeFalse("nothing set, nothing cleared");
        Waits("lab.requirePaymentBeforeCollection", On, JsonValue.Create("yes")).ShouldBeTrue("neither on nor off");
        Waits("billing.discount.defaultMaxPercent", Number(5), JsonValue.Create("ten")).ShouldBeTrue();
    }

    [Fact]
    public void Where_money_patients_data_or_alerts_go_waits_on_any_change()
    {
        Waits("portal.baseUrl", JsonValue.Create("https://care.nanahospitals.in"), JsonValue.Create("https://care.example.in")).ShouldBeTrue();
        Waits("portal.baseUrl", null, JsonValue.Create("https://care.nanahospitals.in")).ShouldBeTrue("set for the first time too");
        Waits("portal.baseUrl", JsonValue.Create("https://care.nanahospitals.in"), JsonValue.Create("https://care.nanahospitals.in")).ShouldBeFalse();
        Waits("doctorfees.tds.percent", Number(10), Number(2)).ShouldBeTrue("less tax withheld than the hospital owes");
        Waits("doctorfees.tds.percent", Number(10), Number(20)).ShouldBeTrue("doctors underpaid");
        Waits("alerts.oncallPhone", JsonValue.Create("+919800000143"), JsonValue.Create("+919800000999")).ShouldBeTrue();
        Waits("alerts.escalateMinutes", null, Number(5)).ShouldBeTrue("when an alert goes further is the alerts' too");
        Waits("integrations.sms", null, JsonNode.Parse("""{"enabled":true,"provider":"msg91"}""")).ShouldBeTrue();
        Waits("abdm.gateway", null, JsonNode.Parse("""{"cmId":"sbx"}""")).ShouldBeTrue();
        Waits("ai.provider", JsonValue.Create("anthropic"), JsonValue.Create("openai")).ShouldBeTrue();

        // Objects compare by what they hold, not how they were typed.
        Waits("integrations.sms", JsonNode.Parse("""{"enabled":true,"provider":"msg91"}"""), JsonNode.Parse("""{ "enabled": true, "provider": "msg91" }"""))
            .ShouldBeFalse();
        SettingGuard.Judge("integrations.sms", null, JsonNode.Parse("{}"), secret: false).Name.ShouldBe("Integration (integrations.sms)");
    }

    [Fact]
    public void A_credential_waits_on_any_change_and_is_never_put_in_words()
    {
        var change = SettingGuard.Judge("integrations.payment.secret", JsonValue.Create("stored-ciphertext"), JsonNode.Parse("""{"keySecret":"rzp-live-123"}"""), secret: true);
        change.Waits.ShouldBeTrue();
        (change.From, change.To).ShouldBe((SettingGuard.SecretWords, SettingGuard.NewSecretWords));
        change.Summary.ShouldNotContain("rzp-live-123");

        // A secret key outside every family is a credential all the same, and so is a value saved as secret.
        SettingGuard.Judge("it.rotation.123.secret", null, JsonValue.Create("k-123"), secret: false).Waits.ShouldBeTrue();
        SettingGuard.Judge("vendor.token", null, JsonValue.Create("k-123"), secret: true).Waits.ShouldBeTrue();
        SettingGuard.Judge("integrations.sms.secret", JsonValue.Create("stored"), null, secret: true).To.ShouldBe("cleared");
    }

    [Fact]
    public void A_setting_that_is_not_guarded_never_waits()
    {
        var change = SettingGuard.Judge("billing.rateList.publish", On, Off, secret: false);
        (change.Waits, change.Guarded).ShouldBe((false, false));
        Waits("ui.theme", JsonValue.Create("light"), null).ShouldBeFalse("cleared, but nothing relies on it");
        Waits("features.telehealth", On, Off).ShouldBeFalse("a feature switched off removes a service, not a control");
        Waits("scheduling.slotMinutes", Number(15), Number(60)).ShouldBeFalse();
    }

    [Fact]
    public void The_subject_is_one_per_setting_and_facility()
    {
        var facility = Guid.Parse("0192a000-0000-7000-8000-0000000000f1");
        SettingGuard.SubjectId("lab.separateVerifier", facility).ShouldBe(SettingGuard.SubjectId("lab.separateVerifier", facility));
        SettingGuard.SubjectId("lab.separateVerifier", facility).ShouldNotBe(SettingGuard.SubjectId("lab.separateVerifier", null));
        SettingGuard.SubjectId("lab.separateVerifier", null).ShouldNotBe(SettingGuard.SubjectId("lab.requirePaymentBeforeCollection", null));
    }
}
