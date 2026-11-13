using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Services;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// The rules of HIP-initiated linking that need no database: how long a kept link token is sent (six months, as NHA's
/// wrapper keeps it), how long a request waits for the gateway's answer before it is marked failed, and the key of a
/// patient's linking lock.
/// </summary>
public sealed class AbdmLinkingRulesTests
{
    private static readonly DateTimeOffset Issued = new(2026, 3, 31, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_link_token_is_sent_for_six_months_after_it_was_issued_and_not_after()
    {
        AbdmLinkingService.IsLinkTokenUsable(Issued, Issued).ShouldBeTrue();
        AbdmLinkingService.IsLinkTokenUsable(Issued, Issued.AddMonths(6).AddTicks(-1)).ShouldBeTrue();
        AbdmLinkingService.IsLinkTokenUsable(Issued, Issued.AddMonths(6)).ShouldBeFalse();
        AbdmLinkingService.IsLinkTokenUsable(Issued, Issued.AddYears(1)).ShouldBeFalse();
    }

    [Fact]
    public void Six_months_are_calendar_months_so_a_token_from_the_end_of_a_month_runs_to_the_end_of_a_short_one()
    {
        // 31 March + 6 months is 30 September: the token is not used on 30 September at the time it was issued.
        AbdmLinkingService.IsLinkTokenUsable(Issued, new DateTimeOffset(2026, 9, 30, 9, 59, 59, TimeSpan.Zero)).ShouldBeTrue();
        AbdmLinkingService.IsLinkTokenUsable(Issued, new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, 15)]
    [InlineData(30, 30)]
    [InlineData(0, 2)]
    [InlineData(-5, 2)]
    [InlineData(100_000, 1440)]
    public void A_request_waits_for_the_gateways_answer_fifteen_minutes_unless_set_between_two_minutes_and_a_day(int? setting, int minutes)
    {
        var settings = setting is { } value ? new AbdmGatewaySettings { AnswerTimeoutMinutes = value } : new AbdmGatewaySettings();
        settings.AnswerTimeout.ShouldBe(TimeSpan.FromMinutes(minutes));
    }

    [Fact]
    public void A_request_marked_unanswered_says_what_did_not_come_and_that_a_late_answer_is_still_taken()
    {
        AbdmUnansweredRequests.LinkMessage(AbdmLinkStatus.AwaitingToken, 15).ShouldBe(
            "ABDM did not send a link token within 15 minutes. Link the records again; if ABDM answers later, its answer is still taken.");
        AbdmUnansweredRequests.LinkMessage(AbdmLinkStatus.Sent, 15).ShouldContain("did not say within 15 minutes whether it linked the records");
        AbdmUnansweredRequests.ConsentMessage(15).ShouldContain("did not say within 15 minutes that it has the request. Ask again");

        // Each fits the request's error column (500 characters).
        new[] { AbdmUnansweredRequests.LinkMessage(AbdmLinkStatus.Sent, 1440), AbdmUnansweredRequests.ConsentMessage(1440) }.ShouldAllBe(m => m.Length <= 500);
    }

    [Fact]
    public void A_patients_linking_lock_has_one_key_on_every_server_and_version_and_another_patient_another()
    {
        var patient = Guid.Parse("01933a4b-0000-7000-8000-0000000000b2");

        // The first eight bytes of SHA-256("abdm.link:{id:N}"), big-endian: servers of two versions, mid-upgrade, take the
        // same lock for the same patient.
        AbdmLinkingService.LinkingLockKey(patient).ShouldBe(87880660739014757L);
        AbdmLinkingService.LinkingLockKey(patient).ShouldNotBe(AbdmLinkingService.LinkingLockKey(Guid.CreateVersion7()));
    }
}
