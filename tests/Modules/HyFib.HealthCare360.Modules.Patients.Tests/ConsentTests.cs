using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

public sealed class ConsentTests
{
    private static readonly DateTimeOffset Given = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Withdrawn = new(2026, 10, 2, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid Staff = Guid.CreateVersion7();

    [Fact]
    public void Withdrawing_a_given_consent_records_when_by_whom_and_why()
    {
        var consent = Consent(granted: true);

        consent.Revoke("  Patient no longer wants records shared  ", Staff, Withdrawn).IsSuccess.ShouldBeTrue();

        consent.RevokedAt.ShouldBe(Withdrawn);
        consent.RevokedByUserId.ShouldBe(Staff);
        consent.RevokedReason.ShouldBe("Patient no longer wants records shared");
        consent.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void An_automatic_withdrawal_has_no_user()
    {
        var consent = Consent(granted: true);

        consent.Revoke("Withdrawn via WhatsApp STOP reply", null, Withdrawn).IsSuccess.ShouldBeTrue();

        consent.RevokedByUserId.ShouldBeNull();
    }

    [Fact]
    public void A_refusal_cannot_be_withdrawn()
    {
        var refusal = Consent(granted: false);

        var result = refusal.Revoke("Changed mind", Staff, Withdrawn);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("consent.not_granted");
        refusal.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public void A_withdrawal_is_final()
    {
        var consent = Consent(granted: true);
        consent.Revoke("First", Staff, Withdrawn);

        var again = consent.Revoke("Second", Guid.CreateVersion7(), Withdrawn.AddHours(1));

        again.Error!.Code.ShouldBe("consent.revoked");
        consent.RevokedReason.ShouldBe("First");
        consent.RevokedByUserId.ShouldBe(Staff);
        consent.RevokedAt.ShouldBe(Withdrawn);
    }

    [Fact]
    public void Standing_permissions_are_withdrawn_as_a_whole_and_stay_or_procedure_consents_one_by_one()
    {
        ConsentType[] standing =
        [
            ConsentType.DataSharing, ConsentType.SmsWhatsAppUpdates, ConsentType.MarketingCommunication, ConsentType.Telehealth,
            ConsentType.Research, ConsentType.Photography,
        ];
        ConsentType[] oneEpisode = [ConsentType.GeneralTreatment, ConsentType.HighRiskProcedure, ConsentType.Anaesthesia, ConsentType.BloodTransfusion];

        standing.ShouldAllBe(t => PatientConsent.IsStandingPermission(t));
        oneEpisode.ShouldAllBe(t => !PatientConsent.IsStandingPermission(t));
        Enum.GetValues<ConsentType>().Except(standing).Except(oneEpisode).ShouldBeEmpty("Decide whether a new consent type is a standing permission.");
    }

    [Fact]
    public void A_consent_given_on_the_patients_behalf_needs_the_signers_name()
    {
        var validator = new CaptureConsentRequestValidator();

        validator.Validate(Capture() with { SignedByRelation = "Son" }).Errors.ShouldContain(e => e.PropertyName == "SignedByName");
        validator.Validate(Capture() with { SignedByRelation = "Son", SignedByName = "Ravi Kumar" }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Capture_fields_are_limited_to_what_the_record_holds()
    {
        var result = new CaptureConsentRequestValidator().Validate(Capture() with
        {
            ConsentVersion = new string('1', 17),
            WitnessName = new string('a', 201),
            SignedByRelation = new string('b', 65),
            SignedByName = new string('c', 201),
            Notes = new string('d', 1001),
        });

        result.Errors.Select(e => e.PropertyName).Distinct()
            .ShouldBe(["ConsentVersion", "WitnessName", "SignedByRelation", "SignedByName", "Notes"], ignoreOrder: true);
    }

    [Fact]
    public void An_unknown_consent_type_is_refused() =>
        new CaptureConsentRequestValidator().Validate(Capture() with { Type = (ConsentType)99 }).Errors.ShouldContain(e => e.PropertyName == "Type");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_withdrawal_needs_a_reason(string reason) =>
        new RevokeConsentRequestValidator().Validate(new RevokeConsentRequest(reason)).IsValid.ShouldBeFalse();

    [Fact]
    public void A_withdrawal_reason_fits_the_record() =>
        new RevokeConsentRequestValidator().Validate(new RevokeConsentRequest(new string('a', 301))).IsValid.ShouldBeFalse();

    private static PatientConsent Consent(bool granted) => new()
    {
        PatientId = Guid.CreateVersion7(),
        Type = ConsentType.DataSharing,
        Granted = granted,
        CapturedAt = Given,
        CapturedByUserId = Staff,
    };

    private static CaptureConsentRequest Capture() => new(ConsentType.DataSharing, null, true, null, null, null, null, null);
}
