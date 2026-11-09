using System.Text;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Privacy.Tests;

/// <summary>DPDP Act s.9: who is a child, what needs a guardian's consent, and what is never allowed.</summary>
public sealed class ChildDataTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    [Fact]
    public void A_child_is_under_eighteen_and_an_unknown_birth_date_is_not_taken_for_one()
    {
        ChildData.IsChild(Today.AddYears(-18), Today).ShouldBeFalse("18 today is an adult");
        ChildData.IsChild(Today.AddYears(-18).AddDays(1), Today).ShouldBeTrue("18 tomorrow is still a child");
        ChildData.IsChild(null, Today).ShouldBeFalse();
    }

    [Fact]
    public void Health_care_needs_no_guardian_and_sharing_research_and_photographs_do()
    {
        var child = Today.AddYears(-10);
        ChildData.WhyRefused(ConsentType.GeneralTreatment, child, Today, guardianVerified: false).ShouldBeNull();
        ChildData.WhyRefused(ConsentType.SmsWhatsAppUpdates, child, Today, guardianVerified: false).ShouldBeNull("reminders are part of the child's care");
        foreach (var type in new[] { ConsentType.DataSharing, ConsentType.Research, ConsentType.Photography })
        {
            ChildData.WhyRefused(type, child, Today, guardianVerified: false).ShouldNotBeNull().Code.ShouldBe(ChildData.GuardianNeededCode);
            ChildData.WhyRefused(type, child, Today, guardianVerified: true).ShouldBeNull();
        }
    }

    [Fact]
    public void Marketing_is_refused_for_a_child_even_with_a_guardian_and_allowed_for_an_adult()
    {
        ChildData.WhyRefused(ConsentType.MarketingCommunication, Today.AddYears(-12), Today, guardianVerified: true).ShouldNotBeNull().Code.ShouldBe(ChildData.MarketingRefusedCode);
        ChildData.WhyRefused(ConsentType.MarketingCommunication, Today.AddYears(-30), Today, guardianVerified: false).ShouldBeNull();
    }
}

public sealed class GuardianConsentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static GuardianConsent Pending(string code)
    {
        var consent = new GuardianConsent { Status = GuardianConsentStatus.Pending, CodeExpiresAt = Now + GuardianConsent.CodeLifetime };
        consent.CodeHash = GuardianConsent.HashOf(consent.Id, code);
        return consent;
    }

    [Fact]
    public void The_right_code_verifies_once_and_the_code_is_not_kept()
    {
        var consent = Pending("123456");
        consent.Confirm(GuardianConsent.HashOf(consent.Id, "123456"), Guid.NewGuid(), Now).IsSuccess.ShouldBeTrue();
        (consent.Status, consent.CodeHash).ShouldBe((GuardianConsentStatus.Verified, (string?)null));
        consent.Confirm(GuardianConsent.HashOf(consent.Id, "123456"), null, Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Five_wrong_tries_or_a_late_code_end_it()
    {
        var consent = Pending("123456");
        for (var i = 0; i < GuardianConsent.MaxAttempts; i++)
        {
            consent.Confirm(GuardianConsent.HashOf(consent.Id, "000000"), null, Now).Error!.Code.ShouldBe(GuardianConsent.WrongCode);
        }

        consent.Status.ShouldBe(GuardianConsentStatus.Expired);
        var late = Pending("654321");
        late.Confirm(GuardianConsent.HashOf(late.Id, "654321"), null, Now + GuardianConsent.CodeLifetime).Error!.Code.ShouldBe(GuardianConsent.ClosedCode);
    }

    [Fact]
    public void A_code_hash_is_salted_with_the_consent()
    {
        var code = "123456";
        GuardianConsent.HashOf(Guid.NewGuid(), code).ShouldNotBe(GuardianConsent.HashOf(Guid.NewGuid(), code));
    }

    [Fact]
    public void A_document_number_is_never_accepted_whole()
    {
        var validator = new StartGuardianConsentRequestValidator();
        validator.Validate(new StartGuardianConsentRequest("A", GuardianRelationship.Parent, "9876543210", true, GuardianVerificationMethod.Document, "Aadhaar", "123412341234"))
            .IsValid.ShouldBeFalse();
        validator.Validate(new StartGuardianConsentRequest("A", GuardianRelationship.Parent, "9876543210", true, GuardianVerificationMethod.Document, "Aadhaar", "1234"))
            .IsValid.ShouldBeTrue();
        validator.Validate(new StartGuardianConsentRequest("A", GuardianRelationship.Parent, "9876543210", false, GuardianVerificationMethod.Otp, null, null))
            .IsValid.ShouldBeFalse("rule 10 asks the hospital to check the guardian is an adult");
    }
}

public sealed class SdfRuleTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    [Theory]
    [InlineData(40, null, null)]
    [InlineData(30, null, 30)]
    [InlineData(20, 30, null)]
    [InlineData(7, 30, 7)]
    [InlineData(3, 7, null)]
    [InlineData(-1, 7, 0)]
    [InlineData(-5, 0, null)]
    public void A_review_reminder_goes_30_and_7_days_before_and_once_overdue(int daysAway, int? alreadySent, int? expected) =>
        SdfService.Due(Today.AddDays(daysAway), Today, alreadySent).ShouldBe(expected);

    [Fact]
    public void The_highest_risk_is_the_worse_of_likelihood_and_impact()
    {
        new Dpia { Risks = [new("a", RiskLevel.Low, RiskLevel.Medium), new("b", RiskLevel.High, RiskLevel.Low)] }.HighestRisk.ShouldBe(RiskLevel.High);
        new Dpia().HighestRisk.ShouldBeNull();
    }

    [Fact]
    public void A_designated_fiduciary_names_a_dpo_based_in_india()
    {
        var validator = new SdfSettingsValidator();
        validator.Validate(new SdfSettings(true, null, new DpoContact("Dr A", null, "dpo@h.in", "9876543210", null, BasedInIndia: false))).IsValid.ShouldBeFalse();
        validator.Validate(new SdfSettings(true, null, new DpoContact("Dr A", null, "dpo@h.in", "9876543210", null, BasedInIndia: true))).IsValid.ShouldBeTrue();
        validator.Validate(new SdfSettings(false, null, new DpoContact(null, null, null, null, null, false))).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Impact_assessment_and_consent_manager_permissions_default_to_administrators()
    {
        var permissions = new PrivacyModule().Permissions.ToDictionary(p => p.Name);
        permissions[PrivacyPermissions.SdfApprove].DefaultRoles!.ShouldBe([SystemRoles.HospitalAdmin]);
        permissions[PrivacyPermissions.ConsentManagersManage].DefaultRoles!.ShouldBe([SystemRoles.HospitalAdmin]);
        permissions[PrivacyPermissions.SdfManage].DefaultRoles!.ShouldNotContain(SystemRoles.FrontOffice);
    }
}

public sealed class ConsentManagerProtocolTests
{
    [Fact]
    public void A_signature_covers_the_timestamp_and_every_byte_of_the_body()
    {
        var body = Encoding.UTF8.GetBytes("{\"eventId\":\"1\"}");
        var signature = ConsentManagerProtocol.Sign("secret", "1700000000", body);
        ConsentManagerProtocol.Verify("secret", "1700000000", body, signature).ShouldBeTrue();
        ConsentManagerProtocol.Verify("secret", "1700000000", body, signature.ToUpperInvariant()).ShouldBeTrue("hex is read in either case");
        ConsentManagerProtocol.Verify("secret", "1700000001", body, signature).ShouldBeFalse();
        ConsentManagerProtocol.Verify("other", "1700000000", body, signature).ShouldBeFalse();
        ConsentManagerProtocol.Verify("secret", "1700000000", Encoding.UTF8.GetBytes("{\"eventId\":\"2\"}"), signature).ShouldBeFalse();
    }

    [Fact]
    public void Only_purposes_outside_health_care_come_from_a_consent_manager()
    {
        ConsentArtefact.IsAcceptedPurpose(ConsentType.DataSharing).ShouldBeTrue();
        ConsentArtefact.IsAcceptedPurpose(ConsentType.GeneralTreatment).ShouldBeFalse();
        ConsentArtefact.IsAcceptedPurpose(ConsentType.Anaesthesia).ShouldBeFalse();
    }

    [Fact]
    public void An_artefact_is_in_force_only_while_active_and_within_its_validity()
    {
        var now = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var artefact = new ConsentArtefact { Status = ConsentArtefactStatus.Active, ValidFrom = now.AddDays(-1), ValidUntil = now.AddDays(1) };
        artefact.IsInForce(now).ShouldBeTrue();
        artefact.IsInForce(now.AddDays(2)).ShouldBeFalse();
    }
}

public sealed class ErasureBackupTextTests
{
    [Fact]
    public void Erased_data_stays_in_backups_for_the_backup_period_and_one_more_day() =>
        ErasureService.BackupWindowDays(30).ShouldBe(31, "backups older than 30 days are deleted by the next nightly backup");

    [Fact]
    public void The_erasure_answer_says_how_long_backups_keep_a_copy_and_that_a_restore_erases_again()
    {
        var text = ErasureService.Describe([ErasureService.NomineeItem], [], backupDays: 30);
        text.ShouldContain("within 31 days");
        text.ShouldContain("carried out again");
        ErasureService.Describe([ErasureService.NomineeItem], []).ShouldNotContain("backups", Case.Insensitive, "unchanged when the period is not given");
    }

    [Fact]
    public void The_ledger_seals_what_was_erased_for_whom_and_when()
    {
        var entry = new ErasureLedgerEntry(Guid.NewGuid(), Guid.NewGuid(), "NHDPR2600001", Guid.NewGuid(), ["portal", "nominee"], new DateTimeOffset(2026, 11, 8, 4, 0, 0, TimeSpan.Zero));
        ErasureLedger.Canonical(entry).ShouldBe(ErasureLedger.Canonical(entry with { Items = ["nominee", "portal"] }), "the order of the items does not matter");
        ErasureLedger.Canonical(entry).ShouldNotBe(ErasureLedger.Canonical(entry with { PatientId = Guid.NewGuid() }));
        ErasureLedger.Canonical(entry).ShouldNotBe(ErasureLedger.Canonical(entry with { Items = ["portal"] }));
    }
}
