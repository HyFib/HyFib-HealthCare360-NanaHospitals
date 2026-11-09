using System.Net;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Sentinel and never events (MAKER-CHECKER.md, slice 14): one does not close straight from Reported, or at all, without
/// its root-cause analysis completed (<c>incident.sentinel_needs_rca</c>); closing it, and re-grading it so it is no longer
/// sentinel (no analysis due), wait for the quality manager (<c>quality.incidents.approve</c>), never the one who asked nor
/// the incident's reporter, and change nothing until then.
/// </summary>
public sealed class SentinelApprovalTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sentinel_event_closes_only_on_its_completed_analysis_and_when_a_second_person_agrees()
    {
        var nurse = await server.SignInAsync(Users.Nurse);
        var quality = await server.SignInAsync(Users.QualityManager);
        var incident = await ReportSentinelAsync(nurse);
        var path = $"/api/v1/quality/incidents/{incident.Id}";

        // Straight from Reported, as a duplicate once could be: refused, with the code, and nothing asked.
        using (var straight = await quality.SendRawAsync(HttpMethod.Post, $"{path}/transition", new TransitionIncidentRequest(IncidentStatus.Closed, "Duplicate report")))
        {
            straight.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await straight.Content.ReadAsStringAsync(Ct)).ShouldContain("incident.sentinel_needs_rca");
        }

        await CompleteAnalysisAsync(quality, incident.Id);

        // Asked: 202 with the approval; the incident stays where it was, and shows the closing waiting.
        var asked = await AskAsync(quality, $"{path}/transition", new TransitionIncidentRequest(IncidentStatus.Closed, "Retained swab removed; counts now witnessed."));
        asked.Kind.ShouldBe("quality.sentinel_close");
        asked.Details.ShouldContain(d => d.Label == "Status" && d.Value == "Investigation → closed");
        asked.Details.ShouldContain(d => d.Label == "Root-cause analysis" && d.Value == "Completed");
        var waiting = await quality.GetAsync<IncidentDetail>(path);
        waiting.Status.ShouldBe(IncidentStatus.Investigation);
        waiting.ClosureAskedAt.ShouldNotBeNull();

        // The one who asked does not approve it.
        using (var own = await quality.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Mine", asked.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await own.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.self");
        }

        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var approved = await admin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Analysis reviewed.", asked.Version));
        approved.Status.ShouldBe(ApprovalStatus.Approved);
        var closed = await quality.GetAsync<IncidentDetail>(path);
        closed.Status.ShouldBe(IncidentStatus.Closed);
        closed.ClosureNote.ShouldBe("Retained swab removed; counts now witnessed.");
        closed.ClosureAskedAt.ShouldBeNull();
        (await AuditAsync(incident.Id, "quality.incident.sentinel_closed")).ShouldContain("approved by");
    }

    [Fact]
    public async Task The_incidents_reporter_does_not_approve_its_closing()
    {
        // The quality manager reported it; the hospital administrator asks; the quality manager is refused and the request
        // waits for someone else.
        var quality = await server.SignInAsync(Users.QualityManager);
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var incident = await ReportSentinelAsync(quality);
        await CompleteAnalysisAsync(admin, incident.Id);
        var asked = await AskAsync(admin, $"/api/v1/quality/incidents/{incident.Id}/transition", new TransitionIncidentRequest(IncidentStatus.Closed, "Closed after review"));

        using (var reporter = await quality.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Agreed", asked.Version)))
        {
            reporter.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await reporter.Content.ReadAsStringAsync(Ct)).ShouldContain("incident.reporter_approves");
        }

        (await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Pending);
        (await admin.GetAsync<IncidentDetail>($"/api/v1/quality/incidents/{incident.Id}")).Status.ShouldBe(IncidentStatus.Investigation);
    }

    [Fact]
    public async Task Re_grading_a_sentinel_event_out_of_sentinel_waits_with_a_reason_and_changes_nothing_until_approved()
    {
        var nurse = await server.SignInAsync(Users.Nurse);
        var quality = await server.SignInAsync(Users.QualityManager);
        var incident = await ReportSentinelAsync(nurse, IncidentSeverity.Death, kind: null);
        var path = $"/api/v1/quality/incidents/{incident.Id}";
        var reported = await quality.GetAsync<IncidentDetail>(path);
        var lowered = new UpdateIncidentRequest(IncidentType.Clinical, IncidentSeverity.Mild, null, false, reported.Location, null, reported.PatientId, null,
            reported.Version);

        using (var noReason = await quality.SendRawAsync(HttpMethod.Put, path, lowered))
        {
            noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await noReason.Content.ReadAsStringAsync(Ct)).ShouldContain("reason");
        }

        var asked = await AskAsync(quality, path, lowered with { Reason = "The patient survived; the death was recorded in error." }, HttpMethod.Put);
        asked.Kind.ShouldBe("quality.sentinel_downgrade");
        asked.Details.ShouldContain(d => d.Label == "Harm grade" && d.Value == "Death → Mild");

        var waiting = await quality.GetAsync<IncidentDetail>(path);
        (waiting.IsSentinel, waiting.Severity).ShouldBe((true, IncidentSeverity.Death));
        waiting.DowngradeAskedAt.ShouldNotBeNull();

        var admin = await server.SignInAsync(Users.HospitalAdmin);
        await admin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Records corrected.", asked.Version));
        var done = await quality.GetAsync<IncidentDetail>(path);
        (done.IsSentinel, done.Severity, done.DowngradeAskedAt).ShouldBe((false, IncidentSeverity.Mild, (DateTimeOffset?)null));
        (await AuditAsync(incident.Id, "quality.incident.sentinel_downgraded")).ShouldContain("Death");
    }

    [Fact]
    public async Task A_re_grading_that_keeps_it_sentinel_or_raises_it_applies_at_once()
    {
        var nurse = await server.SignInAsync(Users.Nurse);
        var quality = await server.SignInAsync(Users.QualityManager);
        var incident = await ReportSentinelAsync(nurse, IncidentSeverity.Severe, kind: null);
        var path = $"/api/v1/quality/incidents/{incident.Id}";
        var reported = await quality.GetAsync<IncidentDetail>(path);

        // Death is graver: no second person.
        await quality.PutAsync(path, new UpdateIncidentRequest(IncidentType.Clinical, IncidentSeverity.Death, null, false, reported.Location, null, reported.PatientId,
            null, reported.Version));
        (await quality.GetAsync<IncidentDetail>(path)).Severity.ShouldBe(IncidentSeverity.Death);
    }

    private static async Task<IncidentReceipt> ReportSentinelAsync(ApiClient reporter, IncidentSeverity severity = IncidentSeverity.Moderate,
        SentinelEventKind? kind = SentinelEventKind.RetainedForeignObject) =>
        await reporter.PostAsync<IncidentReceipt>("/api/v1/quality/incidents", new ReportIncidentRequest(
            IncidentType.Clinical, severity, DateTimeOffset.UtcNow.AddHours(-2), "Theatre 2", null, null, null,
            "A swab was found on the post-operative X-ray after the abdomen had been closed.", "Patient returned to theatre.", null,
            kind, false, false));

    private static async Task CompleteAnalysisAsync(ApiClient quality, Guid incidentId)
    {
        var path = $"/api/v1/quality/incidents/{incidentId}/investigation";
        await quality.PutAsync(path, new SaveInvestigationRequest(RcaMethod.FiveWhys, "A swab was left in the abdomen.",
            ["The count was not done", "The scrub nurse was relieved mid-case", "No handover count", "No policy for relief", "Policy never written"],
            [new RcaFactorInput(ContributingFactorCategory.Process, "No count at handover of the scrub nurse", true)],
            "The count was skipped at the relief of the scrub nurse.", "Count at every handover, witnessed.", null));
        await quality.PostAsync($"{path}/complete", new { });
    }

    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, string path, object body, HttpMethod? method = null)
    {
        using var response = await maker.SendRawAsync(method ?? HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, method ?? HttpMethod.Post, path);
    }

    private async Task<string> AuditAsync(Guid incidentId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return (await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityId == incidentId.ToString() && a.Action == action).SingleAsync(Ct)).Summary!;
    }
}
