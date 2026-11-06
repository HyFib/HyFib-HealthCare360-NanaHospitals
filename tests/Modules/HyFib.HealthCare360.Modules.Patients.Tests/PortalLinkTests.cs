using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>Portal links sent to the patient's registered number (PP-01): who may get one, and when one still works.</summary>
public sealed class PortalLinkTests
{
    private static readonly DateTimeOffset Sent = new(2026, 11, 2, 10, 30, 0, TimeSpan.Zero);
    private const string Mobile = "+919876543210";

    [Fact]
    public void A_patient_in_use_with_a_mobile_number_and_no_protected_category_may_be_sent_one()
    {
        var patient = Patient();

        PortalLinkRules.WhyNotSend(patient).ShouldBeNull();
        PortalLinkRules.MayHaveLinks(patient).ShouldBeTrue();
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("merged")]
    [InlineData("deceased")]
    public void A_record_no_longer_in_use_is_refused(string why)
    {
        var patient = Patient();
        switch (why)
        {
            case "inactive":
                patient.IsActive = false;
                break;
            case "merged":
                patient.MergedIntoPatientId = Guid.CreateVersion7();
                break;
            default:
                patient.IsDeceased = true;
                break;
        }

        PortalLinkRules.WhyNotSend(patient)!.Code.ShouldBe("portal.link_inactive");
        PortalLinkRules.MayHaveLinks(patient).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("+914423456789")] // a landline in Chennai
    [InlineData("+14155550123")] // a number abroad
    public void Without_an_indian_mobile_number_there_is_nothing_to_send_it_to(string? phone)
    {
        var patient = Patient();
        patient.Phone = phone;

        PortalLinkRules.WhyNotSend(patient)!.Code.ShouldBe("portal.link_no_mobile");
    }

    [Theory]
    [InlineData(SensitiveCategory.Hiv)]
    [InlineData(SensitiveCategory.MentalHealth)]
    [InlineData(SensitiveCategory.Mtp)]
    [InlineData(SensitiveCategory.SexualViolence)]
    public void A_record_in_a_protected_category_gets_portal_access_at_the_desk_only(SensitiveCategory category)
    {
        var patient = Patient();
        patient.SensitiveCategories = category;

        var refusal = PortalLinkRules.WhyNotSend(patient)!;

        refusal.Code.ShouldBe("portal.link_sensitive");
        refusal.Message.ShouldBe(
            "This record is in a protected category. Give portal access at the front desk after checking that the number is the patient's own.");
        PortalLinkRules.MayHaveLinks(patient).ShouldBeFalse();
    }

    [Fact]
    public void Three_links_a_day_are_sent_and_the_fourth_waits()
    {
        PortalLinkRules.IsThrottled(0).ShouldBeFalse();
        PortalLinkRules.IsThrottled(2).ShouldBeFalse();
        PortalLinkRules.IsThrottled(3).ShouldBeTrue();
        PortalLinkRules.WindowStart(Sent).ShouldBe(Sent.AddHours(-24));
    }

    [Fact]
    public void A_link_is_active_then_opened_then_expired_after_seven_days()
    {
        var link = Link();

        link.ExpiresAt.ShouldBe(Sent.AddDays(7));
        link.StateAt(Sent.AddMinutes(1)).ShouldBe(PortalLinkState.Active);

        link.MarkOpened(Sent.AddHours(2));
        link.MarkOpened(Sent.AddHours(5));
        link.OpenedAt.ShouldBe(Sent.AddHours(2), "the first code asked for opens it");
        link.StateAt(Sent.AddHours(6)).ShouldBe(PortalLinkState.Opened);

        link.StateAt(Sent.AddDays(7).AddTicks(-1)).ShouldBe(PortalLinkState.Opened);
        link.StateAt(Sent.AddDays(7)).ShouldBe(PortalLinkState.Expired);
        link.IsLive(Sent.AddDays(7)).ShouldBeFalse();
    }

    [Fact]
    public void A_revoked_link_keeps_its_first_reason()
    {
        var link = Link();

        link.Revoke(PortalLink.ReplacedReason, Sent.AddHours(1));
        link.Revoke(PortalLink.NumberChangedReason, Sent.AddHours(2));

        link.StateAt(Sent.AddHours(3)).ShouldBe(PortalLinkState.Revoked);
        link.StateAt(Sent.AddDays(30)).ShouldBe(PortalLinkState.Revoked, "revoked says more than expired");
        (link.RevokedAt, link.RevokedReason).ShouldBe((Sent.AddHours(1), "replaced"));
        link.IsLive(Sent.AddHours(3)).ShouldBeFalse();
    }

    [Fact]
    public void A_link_opens_only_for_its_patient_while_the_record_has_the_number_it_went_to()
    {
        var patient = Patient();
        var link = Link(patient.Id);
        var now = Sent.AddDays(1);

        link.Opens(patient, now).ShouldBeTrue();

        patient.Phone = "+919000000001";
        link.Opens(patient, now).ShouldBeFalse("the number changed after the link was sent");
        patient.Phone = Mobile;

        patient.SensitiveCategories = SensitiveCategory.Hiv;
        link.Opens(patient, now).ShouldBeFalse("the record was put in a protected category after the link was sent");
        patient.SensitiveCategories = SensitiveCategory.None;

        patient.IsDeceased = true;
        link.Opens(patient, now).ShouldBeFalse();
        patient.IsDeceased = false;

        link.Opens(Patient(), now).ShouldBeFalse("another patient with the same number");
        link.Opens(patient, Sent.AddDays(8)).ShouldBeFalse("expired");
    }

    [Theory]
    [InlineData("AbCdEfGhIjKlMnOpQrStUv", true)]
    [InlineData("abc-def_ghi-jkl_mn", true)]
    [InlineData("short", false)]
    [InlineData("has space in the token!", false)]
    [InlineData("../../api/v1/patients/1234", false)]
    [InlineData(null, false)]
    public void Only_a_token_of_the_shape_hc360_makes_is_looked_up(string? token, bool wellFormed) =>
        PortalLink.IsWellFormedToken(token).ShouldBe(wellFormed);

    [Fact]
    public void The_tokens_made_for_links_are_well_formed_and_differ_each_time()
    {
        var first = SecretTokens.NewOpaqueToken(16);
        var second = SecretTokens.NewOpaqueToken(16);

        PortalLink.IsWellFormedToken(first).ShouldBeTrue();
        first.Length.ShouldBe(22, "128 random bits in base64url");
        second.ShouldNotBe(first);
        SecretTokens.Hash(first).Length.ShouldBe(64, "only the SHA-256 is stored");
    }

    [Theory]
    [InlineData(new[] { NotificationChannel.Sms }, true)]
    [InlineData(new[] { NotificationChannel.WhatsApp }, true)]
    [InlineData(new[] { NotificationChannel.Sms, NotificationChannel.WhatsApp }, true)]
    [InlineData(new NotificationChannel[0], false)]
    [InlineData(new[] { NotificationChannel.Sms, NotificationChannel.Sms }, false)]
    [InlineData(new[] { NotificationChannel.Email }, false)]
    [InlineData(new[] { NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.InApp }, false)]
    public void A_link_goes_by_sms_whatsapp_or_both(NotificationChannel[] channels, bool valid) =>
        new SendPortalLinkRequestValidator().Validate(new SendPortalLinkRequest(channels)).IsValid.ShouldBe(valid);

    private static Patient Patient()
    {
        var patient = new Patient { Uhid = "NH26000123", Phone = Mobile, RegisteredFacilityId = Guid.CreateVersion7() };
        patient.SetName(null, "Lakshmi", null, "Narayanan");
        return patient;
    }

    private static PortalLink Link(Guid? patientId = null) => new()
    {
        FacilityId = Guid.CreateVersion7(),
        PatientId = patientId ?? Guid.CreateVersion7(),
        Phone = Mobile,
        TokenHash = SecretTokens.Hash(SecretTokens.NewOpaqueToken(16)),
        Channels = PortalLinkChannels.Sms,
        ExpiresAt = Sent + PortalLink.Validity,
    };
}
