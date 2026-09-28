using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class TelehealthSessionTests
{
    // 10:00 IST on 28 Sep 2026.
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 4, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan EarlyJoin = TimeSpan.FromMinutes(30);

    private static TelehealthSession NewSession() =>
        TelehealthSession.Schedule(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null, Start, Start.AddMinutes(15));

    [Fact]
    public void A_new_session_is_scheduled_and_has_no_link_yet()
    {
        var session = NewSession();

        session.Status.ShouldBe(TelehealthStatus.Scheduled);
        session.JoinTokenHash.ShouldBeNull();
        session.VerifyJoinToken("anything", Start).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void An_end_before_the_start_is_corrected_to_a_15_minute_slot() =>
        TelehealthSession.Schedule(Guid.Empty, Guid.Empty, Guid.Empty, null, Start, Start.AddMinutes(-5)).ScheduledEndAt.ShouldBe(Start.AddMinutes(15));

    [Fact]
    public void An_issued_link_works_until_two_hours_after_the_scheduled_end()
    {
        var session = NewSession();
        var token = session.IssueJoinToken().Value;

        session.JoinTokenHash.ShouldNotBe(token);
        session.JoinTokenExpiresAt.ShouldBe(Start.AddMinutes(15) + TimeSpan.FromHours(2));
        session.VerifyJoinToken(token, Start).IsSuccess.ShouldBeTrue();
        session.VerifyJoinToken(token, session.JoinTokenExpiresAt!.Value.AddSeconds(1)).Error!.Code.ShouldBe("telehealth.link_expired");
    }

    [Fact]
    public void A_wrong_token_is_forbidden()
    {
        var session = NewSession();
        session.IssueJoinToken();

        var result = session.VerifyJoinToken(JoinTokens.NewToken(), Start);

        result.Error!.Code.ShouldBe("telehealth.invalid_link");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public void Issuing_a_new_link_invalidates_the_previous_one()
    {
        var session = NewSession();
        var first = session.IssueJoinToken().Value;
        var second = session.IssueJoinToken().Value;

        session.VerifyJoinToken(first, Start).IsFailure.ShouldBeTrue();
        session.VerifyJoinToken(second, Start).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Patients_may_join_from_the_early_join_time()
    {
        var session = NewSession();

        session.EnsureJoinable(Start - EarlyJoin - TimeSpan.FromMinutes(1), EarlyJoin).Error!.Code.ShouldBe("telehealth.too_early");
        session.EnsureJoinable(Start - EarlyJoin, EarlyJoin).IsSuccess.ShouldBeTrue();
        session.JoinOpensAt(EarlyJoin).ShouldBe(Start - EarlyJoin);
    }

    [Fact]
    public void Waiting_room_follows_the_patient()
    {
        var session = NewSession();

        session.EnterWaitingRoom(Start.AddMinutes(-5));
        session.Status.ShouldBe(TelehealthStatus.WaitingRoom);
        session.PatientWaitingSince.ShouldBe(Start.AddMinutes(-5));

        session.LeaveWaitingRoom();
        session.Status.ShouldBe(TelehealthStatus.Scheduled);
        session.PatientWaitingSince.ShouldBeNull();
    }

    [Fact]
    public void Starting_and_ending_record_the_duration_and_retire_the_link()
    {
        var session = NewSession();
        var token = session.IssueJoinToken().Value;

        session.End(Start, null).Error!.Code.ShouldBe("telehealth.not_started");
        session.Start(Start.AddMinutes(2)).IsSuccess.ShouldBeTrue();
        session.EnterWaitingRoom(Start.AddMinutes(3));
        session.Status.ShouldBe(TelehealthStatus.InProgress);
        session.End(Start.AddMinutes(14), "Patient on 4G; brief freeze").IsSuccess.ShouldBeTrue();

        session.Status.ShouldBe(TelehealthStatus.Completed);
        session.DurationSeconds.ShouldBe(12 * 60);
        session.QualityNotes.ShouldBe("Patient on 4G; brief freeze");
        session.VerifyJoinToken(token, Start.AddMinutes(15)).IsFailure.ShouldBeTrue();
        session.EnsureJoinable(Start.AddMinutes(15), EarlyJoin).Error!.Code.ShouldBe("telehealth.closed");
        session.IssueJoinToken().IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Only_sessions_that_have_not_started_can_be_cancelled()
    {
        var scheduled = NewSession();
        scheduled.IssueJoinToken();
        scheduled.Cancel("Doctor on emergency duty", Start.AddHours(-3)).IsSuccess.ShouldBeTrue();
        scheduled.JoinTokenHash.ShouldBeNull();
        scheduled.Cancel("again", Start).IsSuccess.ShouldBeTrue();

        var running = NewSession();
        running.Start(Start);
        running.Cancel("too late", Start.AddMinutes(1)).Error!.Code.ShouldBe("telehealth.not_cancellable");
    }

    [Fact]
    public void Unstarted_sessions_become_missed_an_hour_after_their_end()
    {
        var session = NewSession();
        var end = Start.AddMinutes(15);

        session.MarkMissedIfOverdue(end + TelehealthSession.MissedAfter).ShouldBeFalse();
        session.MarkMissedIfOverdue(end + TelehealthSession.MissedAfter + TimeSpan.FromMinutes(1)).ShouldBeTrue();
        session.Status.ShouldBe(TelehealthStatus.Missed);

        var started = NewSession();
        started.Start(Start);
        started.MarkMissedIfOverdue(end + TimeSpan.FromHours(5)).ShouldBeFalse();
    }

    [Fact]
    public void Recording_consent_is_recorded_with_who_and_when()
    {
        var session = NewSession();
        var doctor = Guid.CreateVersion7();

        session.RecordConsent(true, doctor, Start).IsSuccess.ShouldBeTrue();

        session.RecordingConsent.ShouldBeTrue();
        session.RecordingConsentByUserId.ShouldBe(doctor);

        var cancelled = NewSession();
        cancelled.Cancel("Patient unwell", Start.AddHours(-1));
        cancelled.RecordConsent(true, doctor, Start).Error!.Code.ShouldBe("telehealth.closed");
    }

    [Fact]
    public void Participants_keep_the_worst_connection_quality_seen()
    {
        var participant = new TelehealthParticipant { Role = TelehealthRole.Guest, JoinedAt = Start };

        participant.RecordQuality(120, 1.5m, 900, false, Start);
        participant.RecordQuality(450, 0.2m, 300, false, Start.AddSeconds(20));
        participant.RecordQuality(-5, 250m, 0, true, Start.AddSeconds(40));

        participant.WorstRoundTripMs.ShouldBe(450);
        participant.MaxPacketLossPercent.ShouldBe(1.5m);
        participant.LowestBitrateKbps.ShouldBe(300);
        participant.AudioOnly.ShouldBeTrue();
        participant.IsWaiting.ShouldBeTrue();
        participant.Admit(Start.AddMinutes(1));
        participant.IsWaiting.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bell\u0007")]
    public void Chat_messages_are_validated(string text) =>
        TelehealthMessage.Create(Guid.CreateVersion7(), TelehealthRole.Patient, null, "Ravi", text, Start).IsFailure.ShouldBeTrue();

    [Fact]
    public void Chat_messages_are_trimmed_and_limited()
    {
        TelehealthMessage.Create(Guid.CreateVersion7(), TelehealthRole.Clinician, null, "Dr. Mehta", "  Take the tablet after food.\n  ", Start)
            .Value.Text.ShouldBe("Take the tablet after food.");
        TelehealthMessage.Create(Guid.CreateVersion7(), TelehealthRole.Clinician, null, "Dr. Mehta", new string('x', TelehealthMessage.MaxLength + 1), Start)
            .IsFailure.ShouldBeTrue();
    }
}
