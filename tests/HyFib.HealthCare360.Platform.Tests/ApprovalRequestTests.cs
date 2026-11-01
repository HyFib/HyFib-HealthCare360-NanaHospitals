using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The rules of a maker-checker request: another person decides it, while it waits and within its time, and only as it was
/// asked for; only the person who asked takes it back.
/// </summary>
public sealed class ApprovalRequestTests
{
    private static readonly Guid Cashier = Guid.Parse("0192a000-0000-7000-8000-00000000a001");
    private static readonly Guid Accountant = Guid.Parse("0192a000-0000-7000-8000-00000000a002");
    private static readonly DateTimeOffset Asked = new(2026, 10, 31, 10, 0, 0, TimeSpan.FromHours(5.5));

    private static ApprovalRequest CreditNote(string payload = """{"billId":"0192a000-0000-7000-8000-00000000b001","kind":"CreditNote","amount":1200.00,"reason":"Wrong rate"}""") =>
        new()
        {
            Kind = "billing.credit_note",
            Title = "Credit note",
            Summary = "Credit note of ₹1,200.00 on bill NHOPB2600012",
            SubjectType = "Bill",
            SubjectId = Guid.Parse("0192a000-0000-7000-8000-00000000b001"),
            SubjectVersion = 4711,
            Payload = payload,
            PayloadHash = ApprovalPayload.Hash(payload),
            Reason = "Wrong rate",
            RequestedBy = Cashier,
            RequestedByName = "Priya (cashier)",
            RequestedAt = Asked,
            ExpiresAt = Asked + ApprovalRequest.Lifetime,
        };

    [Fact]
    public void Another_person_may_decide_a_waiting_request()
    {
        CreditNote().CanBeDecidedBy(Accountant, Asked.AddHours(1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Nobody_decides_their_own_request()
    {
        var decided = CreditNote().CanBeDecidedBy(Cashier, Asked.AddHours(1));

        decided.Error.ShouldNotBeNull().Code.ShouldBe("approval.self");
        decided.Error.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public void A_request_older_than_its_lifetime_is_asked_again()
    {
        var request = CreditNote();

        request.CanBeDecidedBy(Accountant, Asked + ApprovalRequest.Lifetime - TimeSpan.FromMinutes(1)).IsSuccess.ShouldBeTrue();
        request.CanBeDecidedBy(Accountant, Asked + ApprovalRequest.Lifetime).Error.ShouldNotBeNull().Code.ShouldBe("approval.expired");
        request.IsExpired(Asked + ApprovalRequest.Lifetime).ShouldBeTrue();
    }

    [Fact]
    public void A_request_whose_stored_change_no_longer_matches_its_hash_is_not_approved()
    {
        // The amount was raised in the database after the cashier asked: the accountant would approve ₹12,000, not ₹1,200.
        var request = CreditNote();
        request.Payload = request.Payload.Replace("1200.00", "12000.00", StringComparison.Ordinal);

        request.CanBeDecidedBy(Accountant, Asked.AddHours(1)).Error.ShouldNotBeNull().Code.ShouldBe("approval.altered");
    }

    [Fact]
    public void A_decided_request_is_not_decided_again()
    {
        var request = CreditNote();
        request.MarkRejected(Accountant, "Ravi (accountant)", "The rate was right", Asked.AddHours(1));

        request.Status.ShouldBe(ApprovalStatus.Rejected);
        request.DecisionNote.ShouldBe("The rate was right");
        request.CanBeDecidedBy(Accountant, Asked.AddHours(2)).Error.ShouldNotBeNull().Code.ShouldBe("approval.not_pending");
        request.Withdraw(Cashier, Asked.AddHours(2)).Error.ShouldNotBeNull().Code.ShouldBe("approval.not_pending");
    }

    [Fact]
    public void A_decision_is_never_written_over_another()
    {
        // The cashier withdrew the request while the accountant's approval was being made, and the change failed.
        var request = CreditNote();
        request.Withdraw(Cashier, Asked.AddHours(1)).IsSuccess.ShouldBeTrue();

        Should.Throw<InvalidOperationException>(() => request.MarkFailed(Accountant, "Ravi (accountant)", null, "Bill changed.", Asked.AddHours(1)));
        Should.Throw<InvalidOperationException>(() => request.MarkApproved(Accountant, "Ravi (accountant)", null, "NHPCN26000004", Asked.AddHours(1)));
        request.Status.ShouldBe(ApprovalStatus.Withdrawn);
        request.Outcome.ShouldBeNull();
    }

    [Fact]
    public void Only_the_person_who_asked_withdraws_a_request()
    {
        var request = CreditNote();

        request.Withdraw(Accountant, Asked.AddHours(1)).Error.ShouldNotBeNull().Code.ShouldBe("approval.not_maker");
        request.Withdraw(Cashier, Asked.AddHours(1)).IsSuccess.ShouldBeTrue();
        request.Status.ShouldBe(ApprovalStatus.Withdrawn);
        request.DecidedBy.ShouldBe(Cashier);
    }

    [Fact]
    public void An_approval_that_could_not_be_made_keeps_why()
    {
        var request = CreditNote();
        request.MarkFailed(Accountant, "Ravi (accountant)", null, "Bill NHOPB2600012 changed after this was asked for.", Asked.AddHours(1));

        request.Status.ShouldBe(ApprovalStatus.Failed);
        request.Outcome.ShouldBe("Bill NHOPB2600012 changed after this was asked for.");
        request.DecidedByName.ShouldBe("Ravi (accountant)");
    }

    [Fact]
    public void The_stored_change_reads_back_as_the_exact_text_hashed()
    {
        var payload = ApprovalPayload.Write(new { BillId = Guid.Parse("0192a000-0000-7000-8000-00000000b001"), Amount = 1200.00m, Reason = "Wrong rate" });

        payload.ShouldBe("""{"billId":"0192a000-0000-7000-8000-00000000b001","amount":1200.00,"reason":"Wrong rate"}""");
        ApprovalPayload.Hash(payload).ShouldBe(ApprovalPayload.Hash(payload));
        ApprovalPayload.Hash(payload).Length.ShouldBe(64);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Four_eyes_is_checked_by_person(bool samePerson, bool allowed)
    {
        FourEyes.Check(Cashier, samePerson ? Cashier : Accountant).IsSuccess.ShouldBe(allowed);
        FourEyes.Check(null, Cashier).IsSuccess.ShouldBeTrue();
    }
}
