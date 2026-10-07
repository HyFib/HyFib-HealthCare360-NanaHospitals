using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

public sealed class PortalFamilyTests
{
    private static readonly DateTimeOffset Sent = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Staff = Guid.CreateVersion7();
    private static readonly Guid Patient = Guid.CreateVersion7();

    [Fact]
    public void Ending_a_link_records_when_by_whom_and_why()
    {
        var link = Link();

        link.Revoke("  Patient asked at the front desk  ", Staff, Sent.AddDays(3)).IsSuccess.ShouldBeTrue();

        link.IsActive.ShouldBeFalse();
        link.RevokedAt.ShouldBe(Sent.AddDays(3));
        link.RevokedByUserId.ShouldBe(Staff);
        link.RevokedReason.ShouldBe("Patient asked at the front desk");
    }

    [Fact]
    public void An_ended_link_stays_ended()
    {
        var link = Link();
        link.Revoke("First", Staff, Sent.AddDays(1));

        var again = link.Revoke("Second", null, Sent.AddDays(2));

        again.Error!.Code.ShouldBe("portal.family_link_ended");
        link.RevokedReason.ShouldBe("First");
        link.RevokedAt.ShouldBe(Sent.AddDays(1));
    }

    [Fact]
    public void The_right_code_makes_the_link_once()
    {
        var code = Code("482913");

        var verified = code.Verify(SecretTokens.Hash("482913"), Sent.AddMinutes(4));

        verified.Value.ShouldBe(Patient);
        code.ConsumedAt.ShouldBe(Sent.AddMinutes(4));
        code.Verify(SecretTokens.Hash("482913"), Sent.AddMinutes(5)).Error!.Code.ShouldBe("portal.family_code_expired");
    }

    [Fact]
    public void A_wrong_code_counts_as_a_try()
    {
        var code = Code("482913");

        var wrong = code.Verify(SecretTokens.Hash("000000"), Sent.AddMinutes(1));

        wrong.Error!.Fields!.ShouldContainKey("code");
        code.Attempts.ShouldBe(1);
        code.ConsumedAt.ShouldBeNull();
    }

    [Fact]
    public void Five_wrong_tries_use_up_the_code()
    {
        var code = Code("482913");
        for (var i = 0; i < PortalFamilyCode.MaxAttempts; i++)
        {
            code.Verify(SecretTokens.Hash("000000"), Sent.AddMinutes(1));
        }

        code.Verify(SecretTokens.Hash("482913"), Sent.AddMinutes(2)).Error!.Code.ShouldBe("portal.family_code_expired");
    }

    [Fact]
    public void A_code_ends_after_its_lifetime()
    {
        var code = Code("482913");

        code.IsOpen(Sent + PortalFamilyCode.Lifetime).ShouldBeFalse();
        code.Verify(SecretTokens.Hash("482913"), Sent + PortalFamilyCode.Lifetime).Error!.Code.ShouldBe("portal.family_code_expired");
    }

    [Fact]
    public void A_request_that_matched_no_patient_never_succeeds()
    {
        // Nothing was sent, so there is nothing to enter; even a guess that happens to equal an empty hash fails.
        var code = new PortalFamilyCode
        {
            AccountPhone = "+919800000001",
            TargetPhone = "+919800000002",
            CreatedAt = Sent,
            ExpiresAt = Sent + PortalFamilyCode.Lifetime,
        };

        var result = code.Verify(SecretTokens.Hash(""), Sent.AddMinutes(1));

        result.IsFailure.ShouldBeTrue();
        code.Attempts.ShouldBe(1);
    }

    private static PortalFamilyLink Link() => new()
    {
        AccountPhone = "+919800000001",
        PatientId = Patient,
        Relation = FamilyRelation.Parent,
        Source = FamilyLinkSource.FrontDesk,
        LinkedAt = Sent,
        LinkedByUserId = Staff,
        ConsentGivenBy = "Patient",
    };

    private static PortalFamilyCode Code(string secret) => new()
    {
        AccountPhone = "+919800000001",
        TargetPhone = "+919800000002",
        PatientId = Patient,
        Relation = FamilyRelation.Parent,
        CodeHash = SecretTokens.Hash(secret),
        CreatedAt = Sent,
        ExpiresAt = Sent + PortalFamilyCode.Lifetime,
    };
}
