using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Diagnostics;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A correction of a released lab report, where the lab requires a second person to verify (R-6,
/// <c>lab.separateVerifier</c>): whoever enters a value does not also verify it, and a correction enters new values into a
/// report doctors may already have acted on. One pathologist asks for it, and another releases it by approving the
/// request. Until then nothing on the report changes, and every copy says a correction is waiting. A lab with one
/// pathologist, the default, corrects at once.
/// </summary>
public sealed class LabAmendmentApprovalTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> Normal = new() { ["NA"] = "138", ["K"] = "4.2", ["CL"] = "101" };

    [Fact]
    public async Task Where_the_lab_requires_a_second_pathologist_a_correction_waits_for_one_and_names_both()
    {
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPathologist);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var report = await ReleasedAsync();
        var potassium = report.Line("K");

        await using (await SeparateVerifierAsync())
        {
            // The pathologist asks for the correction: it waits for approval, and the report is as it was.
            var asked = await AskAsync(pathologist, report.Id, Correction(potassium, "5.8"));
            (asked.Kind, asked.Status, asked.CanDecide, asked.CanWithdraw).ShouldBe((LabAmendmentApproval.Key, ApprovalStatus.Pending, false, true));
            asked.Summary.ShouldBe($"Correction of lab report {report.Number}");
            asked.Details.ShouldContain(new ApprovalDetail(potassium.TestName, "4.2 mmol/L -> 5.8 mmol/L"));

            var waiting = await ReportAsync(pathologist, report.Id);
            (waiting.Revision, waiting.Amendments.Count, Value(waiting, potassium)).ShouldBe((0, 0, "4.2"));
            waiting.AmendmentWaitingSince.ShouldNotBeNull();

            // So does every copy: the doctor's, which shows released results only, and the printed report.
            (await ReportAsync(doctor, report.Id)).AmendmentWaitingSince.ShouldNotBeNull();
            (await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/lab/reports/{report.Id}/document")).Html
                .ShouldContain("An amendment of this report is waiting for a second pathologist");

            // One correction of a report waits at a time, and the one who asked does not approve it.
            (await RefusedAsync(second, $"/api/v1/lab/reports/{report.Id}/amend", Correction(potassium, "5.9"), HttpStatusCode.Conflict)).ShouldBe("approval.already_requested");
            (await RefusedAsync(pathologist, Approve(asked), new ApproveRequest(null, asked.Version), HttpStatusCode.Forbidden)).ShouldBe("approval.self");

            // The second pathologist finds it in the inbox and approves it: the correction is made then, and names both.
            var inbox = await second.GetAsync<PagedResult<ApprovalRequestDto>>("/api/v1/approvals?pageSize=200");
            inbox.Items.Single(r => r.Id == asked.Id).CanDecide.ShouldBeTrue();
            var approved = await MakerChecker.ApproveAsync(second, asked);
            approved.Outcome.ShouldBe($"Revision 1 of report {report.Number}");

            var amended = await ReportAsync(pathologist, report.Id);
            (amended.Revision, amended.Status, Value(amended, potassium)).ShouldBe((1, LabReportStatus.Amended, "5.8"));
            amended.AmendmentWaitingSince.ShouldBeNull();
            var amendment = amended.Amendments.ShouldHaveSingleItem();
            (amendment.AmendedBy, amendment.RequestedBy).ShouldBe((await NameAsync(second), await NameAsync(pathologist)));
            amendment.Changes.ShouldBe($"{potassium.TestName}: 4.2 mmol/L -> 5.8 mmol/L");
            (await QueryAsync(db => db.Set<LabReportAmendment>().AsNoTracking().Where(a => a.LabReportId == report.Id)
                    .Select(a => new { a.AmendedBy, a.RequestedBy }).SingleAsync(Ct)))
                .ShouldBe(new { AmendedBy = (Guid?)await IdAsync(second), RequestedBy = (Guid?)await IdAsync(pathologist) }, "both named, each as what they did");

            // The corrected value is verified by the one who approved it, who signs the report with the one who released it.
            (await QueryAsync(db => db.Set<LabResult>().IgnoreQueryFilters().AsNoTracking().Where(r => r.Id == potassium.ResultId)
                .Select(r => r.VerifiedBy).SingleAsync(Ct))).ShouldBe(await IdAsync(second));
            amended.Signatories.Select(s => s.UserId).ShouldContain(await IdAsync(second));
            (await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/lab/reports/{report.Id}/document")).Html
                .ShouldContain($"Asked for by {await NameAsync(pathologist)}");
        }
    }

    [Fact]
    public async Task A_correction_asked_for_on_a_report_that_changed_since_is_not_made()
    {
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPathologist);
        var report = await ReleasedAsync();
        var potassium = report.Line("K");

        ApprovalRequestDto asked;
        await using (await SeparateVerifierAsync())
        {
            asked = await AskAsync(pathologist, report.Id, Correction(potassium, "5.8"));
        }

        // A night with one pathologist on duty: the lab turns its rule off, and the second pathologist corrects the
        // report at once, to another value.
        using (var corrected = await second.SendRawAsync(HttpMethod.Post, $"/api/v1/lab/reports/{report.Id}/amend", Correction(potassium, "6.1")))
        {
            corrected.StatusCode.ShouldBe(HttpStatusCode.NoContent, await corrected.Content.ReadAsStringAsync(Ct));
        }

        // The request was for the report as it was: approving it changes nothing, and it is not made.
        (await RefusedAsync(second, Approve(asked), new ApproveRequest(null, asked.Version), HttpStatusCode.UnprocessableEntity)).ShouldBe("approval.stale");
        var current = await ReportAsync(pathologist, report.Id);
        (current.Revision, Value(current, potassium)).ShouldBe((1, "6.1"));
        current.Amendments.ShouldHaveSingleItem().RequestedBy.ShouldBeNull("the second pathologist made it at once");
        current.AmendmentWaitingSince.ShouldBeNull("the request failed");
        (await pathologist.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Failed);
    }

    [Fact]
    public async Task A_critical_corrected_value_is_asked_for_with_the_call_and_the_call_is_registered_when_approved()
    {
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPathologist);
        var report = await ReleasedAsync();
        var potassium = report.Line("K");
        var call = new CriticalCallDetails(
            "Dr. Meena Iyer", "Duty doctor, casualty", null, CommunicationMethod.Phone, DateTimeOffset.UtcNow.AddMinutes(-1), ReadBackConfirmed: true,
            "Repeat sample and ECG advised");

        await using (await SeparateVerifierAsync())
        {
            // A corrected value that is critical is told to the doctor by whoever found it, before anything else: not asked
            // for without the call.
            (await RefusedAsync(pathologist, $"/api/v1/lab/reports/{report.Id}/amend", Correction(potassium, "7.2"), HttpStatusCode.UnprocessableEntity))
                .ShouldBe("lab.critical_call_required");
            var asked = await AskAsync(pathologist, report.Id, Correction(potassium, "7.2") with { CriticalCall = call });
            asked.Details.ShouldContain(d => d.Label == "Critical value told to" && d.Value.StartsWith("Dr. Meena Iyer, ", StringComparison.Ordinal));
            (await CallsAsync(pathologist, potassium.ResultId)).ShouldBeEmpty("nothing is registered until the correction is made");
            (await CriticalAlertsAsync(report.OrderId)).ShouldBeEmpty("no alert of a value not yet on the report");

            await MakerChecker.ApproveAsync(second, asked);
            var registered = (await CallsAsync(pathologist, potassium.ResultId)).ShouldHaveSingleItem();
            registered.InformedPersonName.ShouldBe("Dr. Meena Iyer");
            registered.Finding.ShouldBe($"{potassium.TestName} 7.2 mmol/L (CriticalHigh) (amended)");
            (await QueryAsync(db => db.Set<CriticalCallLog>().IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == registered.Id)
                .Select(c => c.CalledByUserId).SingleAsync(Ct))).ShouldBe(await IdAsync(pathologist), "the one who phoned it in when they found it");

            // The critical-value alert goes out with the committed correction, through the outbox.
            (await CriticalAlertsAsync(report.OrderId)).ShouldHaveSingleItem().ShouldContain("Critical value: Potassium");
        }
    }

    /// <summary>A new patient's electrolytes, entered by the technician, verified and released by the pathologist.</summary>
    private async Task<ReleasedReport> ReleasedAsync()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-ELECTROLYTES");
        var lines = await LabVerificationTests.EnterAsync(lab, tube, Normal);
        var released = await pathologist.PostAsync<VerificationOutcome>(
            "/api/v1/lab/results/verify", new VerifyResultsRequest([.. lines.Select(l => l.ResultId)], null, null, Release: true));
        released.ReleasedItemIds.ShouldNotBeEmpty();
        var sheet = await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results");
        var order = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{sheet.LabOrderId}");
        var report = await ReportAsync(pathologist, order.ReportId.ShouldNotBeNull());
        return new ReleasedReport(report.ReportId, report.ReportNumber, sheet.LabOrderId, lines);
    }

    /// <summary>Turns on the facility's rule that whoever enters a value does not verify it; disposing turns it off again.</summary>
    private async Task<IAsyncDisposable> SeparateVerifierAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await admin.SaveSettingAsync(LabResultService.SeparateVerifierSetting, new SaveSettingRequest(JsonValue.Create(true), facilityId, false, null));
        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(LabResultService.SeparateVerifierSetting, facilityId, Ct);
        });
    }

    private static AmendReportRequest Correction(ResultSheetLine line, string value) =>
        new("Wrong tube read: the analyser printout of this sample says otherwise", [new AmendmentChange(line.ResultId, value, null, null, null)], null);

    /// <summary>Asks for a correction: 202 with the request.</summary>
    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, Guid reportId, AmendReportRequest correction)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/lab/reports/{reportId}/amend", correction);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    private static Task<LabReportView> ReportAsync(ApiClient client, Guid reportId) => client.GetAsync<LabReportView>($"/api/v1/lab/reports/{reportId}");

    private static string? Value(LabReportView report, ResultSheetLine line) =>
        report.Sections.SelectMany(s => s.Tests).SelectMany(t => t.Results).Single(r => r.ResultId == line.ResultId).Value;

    private static async Task<List<CriticalCallDto>> CallsAsync(ApiClient client, Guid resultId) =>
        [.. (await client.GetAsync<PagedResult<CriticalCallDto>>("/api/v1/lab/critical-calls?kind=Laboratory&pageSize=200")).Items.Where(c => c.LabResultId == resultId)];

    private static async Task<string> NameAsync(ApiClient client) => (await client.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName;

    // The test server's two pathologists share a display name ("Demo Pathologist"): who did what is checked by id.
    private static async Task<Guid> IdAsync(ApiClient client) => (await client.GetAsync<AuthUser>("/api/v1/auth/me")).Id;

    private async Task<T> QueryAsync<T>(Func<HospitalDbContext, Task<T>> query)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<HospitalDbContext>());
    }

    /// <summary>The in-app alerts to critical-alert holders queued about this order's results (sent once committed).</summary>
    private async Task<List<string>> CriticalAlertsAsync(Guid orderId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var queued = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == "diagnostics.staff-alert").Select(m => m.Payload).ToListAsync(Ct);
        return [.. queued.Where(p => p.Contains($"/lab/orders/{orderId}", StringComparison.Ordinal))];
    }

    private static string Approve(ApprovalRequestDto request) => $"/api/v1/approvals/{request.Id}/approve";

    /// <summary>Posts to <paramref name="path"/>, is refused with <paramref name="status"/>, and returns the refusal's code.</summary>
    private static async Task<string?> RefusedAsync(ApiClient client, string path, object body, HttpStatusCode status)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private sealed record ReleasedReport(Guid Id, string Number, Guid OrderId, IReadOnlyList<ResultSheetLine> Lines)
    {
        public ResultSheetLine Line(string testCode) => Lines.Single(l => l.TestCode == testCode);
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
