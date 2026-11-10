using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>A patient's policy is verified by a second person: whoever recorded its terms, or last changed them, may not.</summary>
public sealed class PatientPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Whoever_recorded_its_terms_may_not_verify_it_and_a_change_of_them_withdraws_a_verification()
    {
        var recorder = Guid.CreateVersion7();
        var verifier = Guid.CreateVersion7();
        var policy = new PatientPolicy { CreatedBy = recorder, TermsRecordedBy = recorder };
        policy.RecordedBy(recorder).ShouldBeTrue();
        policy.RecordedBy(verifier).ShouldBeFalse();
        policy.RecordedBy(null).ShouldBeFalse();

        policy.MarkVerified(verifier, "  Confirmed on the TPA portal  ", Now);
        (policy.VerifiedAt, policy.VerifiedBy, policy.VerificationNotes).ShouldBe((Now, verifier, "Confirmed on the TPA portal"));

        // The verifier extends its validity: the verification is withdrawn, and now the verifier may not verify it.
        policy.TermsChanged(verifier);
        (policy.VerifiedAt, policy.VerifiedBy, policy.VerificationNotes, policy.TermsRecordedBy).ShouldBe((null, null, null, verifier));
        policy.RecordedBy(verifier).ShouldBeTrue();
        policy.RecordedBy(recorder).ShouldBeFalse();

        // A policy recorded before the terms' recorder was kept: its creator stands for it.
        new PatientPolicy { CreatedBy = recorder }.RecordedBy(recorder).ShouldBeTrue();
    }
}
