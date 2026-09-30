using HyFib.HealthCare360.Modules.Privacy.Domain;

namespace HyFib.HealthCare360.Modules.Privacy.Tests;

public sealed class DataRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Officer = Guid.CreateVersion7();

    private static DataRequest Received(DataRequestType type = DataRequestType.Correction, int responseDays = 30)
    {
        var request = new DataRequest { Type = type, PrincipalName = "Asha Rao", Details = "Please fix my date of birth" };
        request.Receive(Now, responseDays, null, "through the patient portal");
        return request;
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(90, 90)]
    [InlineData(120, 90)]
    [InlineData(0, 1)]
    public void The_deadline_is_the_published_period_at_most_90_days(int published, int days) =>
        Received(responseDays: published).DueAt.ShouldBe(Now.AddDays(days));

    [Fact]
    public void A_new_request_is_open_and_says_it_arrived()
    {
        var request = Received();

        request.Status.ShouldBe(DataRequestStatus.Submitted);
        request.IsOpen.ShouldBeTrue();
        request.Events.ShouldHaveSingleItem().IsPublic.ShouldBeTrue();
    }

    [Fact]
    public void It_is_overdue_only_while_open_and_past_the_deadline()
    {
        var request = Received(DataRequestType.Grievance);

        request.IsOverdue(request.DueAt.AddMinutes(1)).ShouldBeTrue();
        request.Close(DataRequestStatus.Completed, "We have looked into this.", null, Officer, Now).IsSuccess.ShouldBeTrue();
        request.IsOverdue(request.DueAt.AddMinutes(1)).ShouldBeFalse();
    }

    [Fact]
    public void Starting_takes_the_request_up_once()
    {
        var request = Received();

        request.Start(Officer, Now).IsSuccess.ShouldBeTrue();
        request.AssignedToUserId.ShouldBe(Officer);
        request.Start(Officer, Now).Error!.Code.ShouldBe("privacy.request_not_new");
    }

    [Fact]
    public void Data_is_not_changed_for_an_unchecked_requester()
    {
        var request = Received(DataRequestType.Correction);
        request.Corrections.Add(new CorrectionItem { Field = CorrectableFields.DateOfBirth, RequestedValue = "12 Mar 1980" });
        request.DecideCorrections([(0, CorrectionDecision.Accepted, null)], Officer, Now).IsSuccess.ShouldBeTrue();

        request.Close(DataRequestStatus.Completed, "Corrected.", null, Officer, Now).Error!.Code.ShouldBe("privacy.identity_unverified");

        request.VerifyIdentity(IdentityCheck.PhotoId, "Aadhaar card seen", Officer, Now).IsSuccess.ShouldBeTrue();
        request.Close(DataRequestStatus.Completed, "Corrected.", null, Officer, Now).IsSuccess.ShouldBeTrue();
        request.IdentityVerifiedByUserId.ShouldBe(Officer);
    }

    [Fact]
    public void A_request_can_be_declined_without_a_checked_identity()
    {
        var request = Received(DataRequestType.Erasure);

        request.Close(DataRequestStatus.Declined, "We could not confirm who you are. Please visit the front desk with photo ID.", null, Officer, Now)
            .IsSuccess.ShouldBeTrue();
        request.Status.ShouldBe(DataRequestStatus.Declined);
    }

    [Fact]
    public void A_grievance_can_be_answered_without_a_checked_identity() =>
        Received(DataRequestType.Grievance).Close(DataRequestStatus.Completed, "We have stopped the messages.", null, Officer, Now).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Every_answer_needs_a_reply(string? response) =>
        Received(DataRequestType.Grievance).Close(DataRequestStatus.Completed, response, null, Officer, Now).Error!.Fields!.ShouldContainKey("response");

    [Fact]
    public void Only_completed_partly_completed_or_declined_close_a_request() =>
        Received(DataRequestType.Grievance).Close(DataRequestStatus.Withdrawn, "No", null, Officer, Now).Error!.Fields!.ShouldContainKey("outcome");

    [Fact]
    public void Every_change_must_be_decided_before_a_correction_is_completed()
    {
        var request = Received(DataRequestType.Correction);
        request.Corrections.AddRange(
        [
            new CorrectionItem { Field = CorrectableFields.Name, RequestedValue = "Asha R. Rao" },
            new CorrectionItem { Field = CorrectableFields.Email, RequestedValue = "asha@example.in" },
        ]);
        request.VerifyIdentity(IdentityCheck.KnownToStaff, null, Officer, Now);
        request.DecideCorrections([(0, CorrectionDecision.Accepted, null)], Officer, Now);

        request.Close(DataRequestStatus.Completed, "Done.", null, Officer, Now).Error!.Code.ShouldBe("privacy.corrections_pending");
    }

    [Fact]
    public void A_declined_change_needs_a_reason()
    {
        var request = Received(DataRequestType.Correction);
        request.Corrections.Add(new CorrectionItem { Field = CorrectableFields.HealthRecord, RequestedValue = "Remove the diabetes diagnosis" });

        request.DecideCorrections([(0, CorrectionDecision.Declined, null)], Officer, Now).Error!.Fields!.ShouldContainKey("items");
        request.DecideCorrections([(0, CorrectionDecision.Declined, "The diagnosis is confirmed by your HbA1c results.")], Officer, Now)
            .IsSuccess.ShouldBeTrue();
        request.Corrections[0].Decision.ShouldBe(CorrectionDecision.Declined);
    }

    [Fact]
    public void Decisions_must_refer_to_the_requested_changes()
    {
        var request = Received(DataRequestType.Correction);

        request.DecideCorrections([(3, CorrectionDecision.Accepted, null)], Officer, Now).Error!.Fields!.ShouldContainKey("items");
        Received(DataRequestType.Erasure).DecideCorrections([(0, CorrectionDecision.Accepted, null)], Officer, Now).Error!.Code.ShouldBe("privacy.not_correction");
    }

    [Fact]
    public void An_access_request_is_completed_with_the_summary_it_gave()
    {
        var request = Received(DataRequestType.Access);
        request.VerifyIdentity(IdentityCheck.PortalSignIn, null, null, Now);

        request.Close(DataRequestStatus.Completed, "Here is your summary.", null, Officer, Now).Error!.Code.ShouldBe("privacy.summary_missing");
        request.Close(DataRequestStatus.Completed, "Here is your summary.", "{\"categories\":[]}", Officer, Now).IsSuccess.ShouldBeTrue();
        request.SummarySnapshot.ShouldBe("{\"categories\":[]}");
    }

    [Fact]
    public void Erasure_is_recorded_only_for_an_open_checked_erasure_request()
    {
        var request = Received(DataRequestType.Erasure);

        request.CanRecordErasure().Error!.Code.ShouldBe("privacy.identity_unverified");
        request.VerifyIdentity(IdentityCheck.PortalSignIn, null, null, Now);
        request.RecordErasure(["messages"], "Erased: optional messages stopped.", new DateOnly(2031, 3, 12), false, Officer, Now).IsSuccess.ShouldBeTrue();
        request.RecordErasure(["nominee"], "Erased: nominee removed.", new DateOnly(2031, 3, 12), false, Officer, Now).IsSuccess.ShouldBeTrue();

        request.ErasedItems.ShouldBe(["messages", "nominee"]);
        request.RetainedUntil.ShouldBe(new DateOnly(2031, 3, 12));
        Received(DataRequestType.Grievance).CanRecordErasure().Error!.Code.ShouldBe("privacy.not_erasure");
    }

    [Fact]
    public void Only_an_open_request_can_be_withdrawn()
    {
        var request = Received();

        request.Withdraw(null, Now).IsSuccess.ShouldBeTrue();
        request.Status.ShouldBe(DataRequestStatus.Withdrawn);
        request.Withdraw(null, Now).Error!.Code.ShouldBe("privacy.request_closed");
    }

    [Fact]
    public void Staff_notes_are_not_shown_to_the_principal()
    {
        var request = Received();

        request.AddNote("Called the patient; they will bring ID tomorrow.", Officer, Now).IsSuccess.ShouldBeTrue();

        request.Events.Last().Kind.ShouldBe(DataRequestEventKind.Note);
        request.Events.Last().IsPublic.ShouldBeFalse();
    }
}
