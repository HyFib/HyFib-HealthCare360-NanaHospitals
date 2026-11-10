using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Housekeeping;
using HyFib.HealthCare360.Modules.SupportServices.Features.Housekeeping;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Housekeeping beyond the bed turnaround, over the real API: areas cleaned on a schedule against their checklists, the
/// day's board, spill and terminal cleaning asked for by the wards with the response time measured against each kind's
/// target, and every cleaning verified by a supervisor who did not do it (a cleaning sent back is done again).
/// </summary>
public sealed class HousekeepingTests(HospitalServer server)
{
    private const string Base = "/api/v1/housekeeping";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scheduled_cleaning_is_done_against_its_checklist_and_verified_only_by_someone_who_did_not_do_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var supervisor = await server.SignInAsync(HospitalServer.Users.SecondNurse);
        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        string[] checklist = ["High-touch surfaces wiped", "Floor mopped with disinfectant", "Bins emptied"];

        // Not the nurses' to set up.
        var area = new SaveCleaningAreaRequest($"ICU-{Random.Shared.Next(1000, 9999)}", "ICU bay 2", null, CleaningRiskZone.High, CleaningFrequency.Daily, 2, null, checklist, true, null);
        (await RefusalAsync(nurse, HttpMethod.Post, $"{Base}/areas", area)).Status.ShouldBe(HttpStatusCode.Forbidden);
        var created = await admin.PostAsync<CleaningAreaDto>($"{Base}/areas", area);

        var board = await nurse.GetAsync<CleaningBoard>($"{Base}/board?date={today:yyyy-MM-dd}");
        board.Slots.Where(s => s.AreaId == created.Id).Select(s => s.Slot).ShouldBe([1, 2]);

        // Every item answered, and one not done says why.
        (await RefusalAsync(nurse, HttpMethod.Post, Cleanings(created.Id), new RecordCleaningRequest(today, 1, [new ChecklistResult(checklist[0], true)], null)))
            .ShouldBe((HttpStatusCode.BadRequest, "validation"));
        (await RefusalAsync(nurse, HttpMethod.Post, Cleanings(created.Id), new RecordCleaningRequest(today, 1, Answers(checklist, notDone: checklist[2]), null)))
            .ShouldBe((HttpStatusCode.BadRequest, "validation"));
        var done = await nurse.PostAsync<CleaningRecordDto>(Cleanings(created.Id),
            new RecordCleaningRequest(today, 1, Answers(checklist, notDone: checklist[2], note: "Bin liners out of stock"), null));
        (done.Status, done.CleanedByYou).ShouldBe((CleaningStatus.AwaitingVerification, true));
        (await RefusalAsync(nurse, HttpMethod.Post, Cleanings(created.Id), new RecordCleaningRequest(today, 1, Answers(checklist), null)))
            .ShouldBe((HttpStatusCode.Conflict, "housekeeping.slot_done"));

        // The cleaner does not verify their own cleaning.
        (await RefusalAsync(nurse, HttpMethod.Post, $"{Base}/cleanings/{done.Id}/verify", new VerifyCleaningRequest(true, null, done.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "housekeeping.verify_own"));

        // The supervisor sends it back with what was not clean; it is done again and verified.
        var redo = await supervisor.PostAsync<CleaningRecordDto>($"{Base}/cleanings/{done.Id}/verify", new VerifyCleaningRequest(false, "Bins still full", done.Version));
        redo.Status.ShouldBe(CleaningStatus.Redo);
        board = await nurse.GetAsync<CleaningBoard>($"{Base}/board?date={today:yyyy-MM-dd}");
        board.Slots.Single(s => s.AreaId == created.Id && s.Slot == 1).Record.ShouldNotBeNull().Status.ShouldBe(CleaningStatus.Redo);
        var again = await nurse.PostAsync<CleaningRecordDto>(Cleanings(created.Id), new RecordCleaningRequest(today, 1, Answers(checklist), "Liners from the store"));
        var verified = await supervisor.PostAsync<CleaningRecordDto>($"{Base}/cleanings/{again.Id}/verify", new VerifyCleaningRequest(true, null, again.Version));
        verified.Status.ShouldBe(CleaningStatus.Verified);
        verified.VerifiedByName.ShouldNotBeNull().ShouldNotBe(verified.CleanedByName);
    }

    [Fact]
    public async Task A_spill_is_answered_against_its_target_cleaned_against_its_checklist_and_verified_by_someone_else()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var supervisor = await server.SignInAsync(HospitalServer.Users.SecondNurse);

        var spill = await doctor.PostAsync<CleaningRequestDto>($"{Base}/requests", new RaiseCleaningRequest(CleaningRequestKind.Spill, null, "Ward 4, bed 12", "Blood on the floor"));
        (spill.Status, spill.TargetMinutes, spill.ChecklistItems.Count).ShouldBe((CleaningRequestStatus.Open, 15, 5));

        // Nobody answered for 20 minutes: the response is late.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<CleaningRequest>().IgnoreQueryFilters().Where(r => r.Id == spill.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.RequestedAt, DateTimeOffset.UtcNow.AddMinutes(-20)), Ct);
        }

        var open = (await nurse.GetAsync<List<CleaningRequestDto>>($"{Base}/requests")).Single(r => r.Id == spill.Id);
        open.ResponseLate.ShouldBeTrue();
        var acknowledged = await nurse.PostAsync<CleaningRequestDto>($"{Base}/requests/{spill.Id}/acknowledge", new CleaningRequestStepRequest(open.Version));
        acknowledged.ResponseMinutes.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(19);
        acknowledged.ResponseLate.ShouldBeTrue();

        var done = await nurse.PostAsync<CleaningRequestDto>($"{Base}/requests/{spill.Id}/complete",
            new CompleteCleaningRequest(Answers([.. spill.ChecklistItems]), null, acknowledged.Version));
        done.Status.ShouldBe(CleaningRequestStatus.AwaitingVerification);
        (await RefusalAsync(nurse, HttpMethod.Post, $"{Base}/requests/{spill.Id}/verify", new VerifyCleaningRequest(true, null, done.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "housekeeping.verify_own"));
        var verified = await supervisor.PostAsync<CleaningRequestDto>($"{Base}/requests/{spill.Id}/verify", new VerifyCleaningRequest(true, null, done.Version));
        verified.Status.ShouldBe(CleaningRequestStatus.Verified);

        var report = await supervisor.GetAsync<HousekeepingReport>($"{Base}/report");
        report.Requests.Single(k => k.Kind == CleaningRequestKind.Spill).ShouldSatisfyAllConditions(
            k => k.Requests.ShouldBeGreaterThanOrEqualTo(1),
            k => k.Late.ShouldBeGreaterThanOrEqualTo(1));
    }

    private static string Cleanings(Guid areaId) => $"{Base}/areas/{areaId}/cleanings";

    private static List<ChecklistResult> Answers(IEnumerable<string> items, string? notDone = null, string? note = null) =>
        [.. items.Select(i => i == notDone ? new ChecklistResult(i, false, note) : new ChecklistResult(i, true))];

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }
}
