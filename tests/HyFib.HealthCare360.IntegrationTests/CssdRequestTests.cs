using System.Net;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;
using HyFib.HealthCare360.Platform.Http;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Requests for sterile sets from departments (CSSD-02) through the API, as a ward and the CSSD issue counter use them:
/// a request is open until packs are issued against it, part issued while some are still owed, issued when every line
/// is filled, and a filled or cancelled request takes no more. Two counters issuing against one request at the same
/// moment count every pack they issue.
/// </summary>
public sealed class CssdRequestTests(HospitalServer server)
{
    private const string Requests = "/api/v1/cssd/requests";
    private const string Issues = "/api/v1/cssd/issues";
    private const string Loads = "/api/v1/cssd/loads";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_is_part_issued_then_issued_as_packs_go_against_it_and_a_closed_one_takes_no_more()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var cssd = await server.SignInAsync(HospitalServer.Users.CssdTechnician);
        var (major, dressing) = await SteamSetsAsync(cssd);
        var (surgery, theatre) = await DepartmentsAsync();

        // The surgical ward asks for two major trays and a dressing set; a set asked for twice is one line.
        var request = await nurse.PostAsync<CssdRequestDto>(Requests, new CreateCssdRequestRequest(surgery, null, CssdPriority.Routine, null,
            [new RequestLineInput(major.Id, 1), new RequestLineInput(dressing.Id, 1), new RequestLineInput(major.Id, 1)]));
        request.Status.ShouldBe(CssdRequestStatus.Open);
        Lines(request).ShouldBe([(major.Id, 2, 0), (dressing.Id, 1, 0)], ignoreOrder: true);

        var majors = await ReleasedPacksAsync(cssd, major.Id, 3);
        var dressings = await ReleasedPacksAsync(cssd, dressing.Id, 1);

        // One major tray goes: the request is part issued, and still open for the rest.
        await cssd.PostAsync<List<SetIssueDto>>(Issues, new IssuePacksRequest(surgery, request.Id, "Staff nurse Leela", [majors[0]]));
        request = await RequestAsync(nurse, request);
        request.Status.ShouldBe(CssdRequestStatus.PartiallyIssued);
        Lines(request).ShouldBe([(major.Id, 2, 1), (dressing.Id, 1, 0)], ignoreOrder: true);

        // Packs for another department cannot fill it.
        await RefusedAsync(cssd, new IssuePacksRequest(theatre, request.Id, null, [majors[1]]), HttpStatusCode.BadRequest, "cssdRequestId");

        // The second tray and the dressing set fill it.
        await cssd.PostAsync<List<SetIssueDto>>(Issues, new IssuePacksRequest(surgery, request.Id, "Staff nurse Leela", [majors[1], dressings[0]]));
        request = await RequestAsync(nurse, request);
        request.Status.ShouldBe(CssdRequestStatus.Issued);
        Lines(request).ShouldBe([(major.Id, 2, 2), (dressing.Id, 1, 1)], ignoreOrder: true);

        // A filled request takes no more, and the pack refused stays on the shelf.
        await RefusedAsync(cssd, new IssuePacksRequest(surgery, request.Id, null, [majors[2]]), HttpStatusCode.BadRequest, "cssdRequestId");
        await RefusedAsync(nurse, $"{Requests}/{request.Id}/cancel", new CancelCssdRequestRequest("Not needed"), "cssd.request_closed");
        (await cssd.GetAsync<List<SterilePackDto>>($"/api/v1/cssd/packs?setId={major.Id}")).ShouldContain(p => p.Id == majors[2]);

        // The theatre cancels its request: it is closed, with the reason, and takes no packs.
        var cancelled = await nurse.PostAsync<CssdRequestDto>(Requests,
            new CreateCssdRequestRequest(theatre, null, CssdPriority.Urgent, "List for tomorrow", [new RequestLineInput(major.Id, 1)]));
        using (var cancel = await nurse.SendRawAsync(HttpMethod.Post, $"{Requests}/{cancelled.Id}/cancel", new CancelCssdRequestRequest("Surgery postponed")))
        {
            cancel.EnsureSuccessStatusCode();
        }

        cancelled = await RequestAsync(nurse, cancelled);
        (cancelled.Status, cancelled.CancelReason).ShouldBe((CssdRequestStatus.Cancelled, "Surgery postponed"));
        await RefusedAsync(nurse, $"{Requests}/{cancelled.Id}/cancel", new CancelCssdRequestRequest("Twice"), "cssd.request_closed");
        await RefusedAsync(cssd, new IssuePacksRequest(theatre, cancelled.Id, null, [majors[2]]), HttpStatusCode.BadRequest, "cssdRequestId");
    }

    [Fact]
    public async Task Two_counters_issuing_against_one_request_at_once_count_every_pack_they_issue()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var cssd = await server.SignInAsync(HospitalServer.Users.CssdTechnician);
        var (major, _) = await SteamSetsAsync(cssd);
        var (surgery, _) = await DepartmentsAsync();
        var packs = await ReleasedPacksAsync(cssd, major.Id, 5);

        // Five trays asked for; one already sent, so the request is part issued before the rush.
        var request = await nurse.PostAsync<CssdRequestDto>(Requests,
            new CreateCssdRequestRequest(surgery, null, CssdPriority.Urgent, null, [new RequestLineInput(major.Id, 5)]));
        await cssd.PostAsync<List<SetIssueDto>>(Issues, new IssuePacksRequest(surgery, request.Id, null, [packs[0]]));

        // Four counters scan a tray each against it at the same moment. However many go through, the request counts them.
        var responses = await Task.WhenAll(packs.Skip(1).Select(pack =>
            cssd.SendRawAsync(HttpMethod.Post, Issues, new IssuePacksRequest(surgery, request.Id, null, [pack]))));
        try
        {
            var issued = responses.Count(r => r.IsSuccessStatusCode);
            issued.ShouldBeGreaterThan(0);
            responses.Where(r => !r.IsSuccessStatusCode).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);

            request = await RequestAsync(nurse, request);
            request.Lines.ShouldHaveSingleItem().IssuedQuantity.ShouldBe(1 + issued, "Every pack issued against the request is counted.");
            request.Status.ShouldBe(issued == 4 ? CssdRequestStatus.Issued : CssdRequestStatus.PartiallyIssued);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static (Guid SetId, int Quantity, int Issued)[] Lines(CssdRequestDto request) =>
        [.. request.Lines.Select(l => (l.InstrumentSetId, l.Quantity, l.IssuedQuantity))];

    /// <summary>The request as the department's list shows it now.</summary>
    private static async Task<CssdRequestDto> RequestAsync(ApiClient client, CssdRequestDto request) =>
        (await client.GetAsync<List<CssdRequestDto>>($"{Requests}?departmentId={request.DepartmentId}")).Single(r => r.Id == request.Id);

    /// <summary>Steam-sterilized packs of a set, released and on the shelf.</summary>
    private static async Task<List<Guid>> ReleasedPacksAsync(ApiClient cssd, Guid setId, int count)
    {
        var load = await cssd.PostAsync<LoadDto>(Loads,
            new CreateLoadRequest("Autoclave 2", SterilizationMethod.Steam, "134 °C instruments", false, null, null, [new LoadPackInput(setId, count)]));
        await cssd.PostAsync<LoadDto>($"{Loads}/{load.Id}/start");
        await cssd.PostAsync<LoadDto>($"{Loads}/{load.Id}/complete", new CompleteLoadRequest(134m, 210m, 4, IndicatorResult.Pass));
        var released = await cssd.PostAsync<LoadDto>($"{Loads}/{load.Id}/release");
        released.Status.ShouldBe(LoadStatus.Released);
        return [.. released.Packs.Select(p => p.Id)];
    }

    private static async Task RefusedAsync(ApiClient client, IssuePacksRequest issue, HttpStatusCode status, string reason)
    {
        using var refused = await client.SendRawAsync(HttpMethod.Post, Issues, issue);
        refused.StatusCode.ShouldBe(status);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(reason);
    }

    private static async Task RefusedAsync(ApiClient client, string path, object body, string code)
    {
        using var refused = await client.SendRawAsync(HttpMethod.Post, path, body);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(code);
    }

    /// <summary>Two of the seeded steam sets: the major surgical tray and the dressing set.</summary>
    private static async Task<(InstrumentSetDto Major, InstrumentSetDto Dressing)> SteamSetsAsync(ApiClient cssd)
    {
        var sets = await cssd.GetAsync<List<InstrumentSetDto>>("/api/v1/cssd/sets");
        return (sets.Single(s => s.Code == "MAJOR-TRAY"), sets.Single(s => s.Code == "DRESSING-SET"));
    }

    /// <summary>The seeded General Surgery department (its ward) and the operation theatre.</summary>
    private async Task<(Guid Surgery, Guid Theatre)> DepartmentsAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var departments = await admin.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments");
        return (departments.Single(d => d.Code == "GS").Id, departments.Single(d => d.Code == "OT").Id);
    }
}
