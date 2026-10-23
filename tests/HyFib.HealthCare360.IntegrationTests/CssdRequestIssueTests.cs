using System.Net;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Requests for sterile sets from departments (CSSD-02) and the packs CSSD issues against them (CSSD-04), over the real API
/// and database. A request is issued in part, then in full, and then takes no more packs; a pack more than a line asks for
/// is issued to the department but not counted against the request. A request can be cancelled while it is open or part
/// issued, never once issued or cancelled, and a cancelled one takes no packs. Only sterile packs within their expiry are
/// issued, and a refused pack refuses the whole issue.
/// </summary>
public sealed class CssdRequestIssueTests(HospitalServer server)
{
    private const string Requests = "/api/v1/cssd/requests";
    private const string Issues = "/api/v1/cssd/issues";
    private const string Loads = "/api/v1/cssd/loads";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_is_issued_in_part_then_in_full_and_then_takes_no_more_packs()
    {
        var (nurse, cssd) = await SignInAsync();
        var (major, dressing) = await SteamSetsAsync(cssd);
        var (theatre, ward) = await DepartmentsAsync(cssd);
        var majors = await ReleasedPacksAsync(cssd, major, 3);
        var dressings = await ReleasedPacksAsync(cssd, dressing, 1);

        var request = await nurse.PostAsync<CssdRequestDto>(Requests, new CreateCssdRequestRequest(
            theatre, null, CssdPriority.Routine, "List of the day", [new RequestLineInput(major, 2), new RequestLineInput(dressing, 1)]));
        request.Status.ShouldBe(CssdRequestStatus.Open);
        Lines(request).ShouldBe([(major, 2, 0), (dressing, 1, 0)], ignoreOrder: true);

        // Another department cannot be given the theatre's request.
        await RefusedAsync(cssd, new IssuePacksRequest(ward, request.Id, null, [majors[0]]), HttpStatusCode.BadRequest, "open request from the same department");

        // One major tray now: part issued.
        await IssueAsync(cssd, theatre, request.Id, majors[0]);
        request = await RequestAsync(nurse, theatre, request.Id);
        request.Status.ShouldBe(CssdRequestStatus.PartiallyIssued);
        Lines(request).ShouldBe([(major, 2, 1), (dressing, 1, 0)], ignoreOrder: true);

        // The second tray and the dressing set: issued in full.
        await IssueAsync(cssd, theatre, request.Id, majors[1], dressings[0]);
        request = await RequestAsync(nurse, theatre, request.Id);
        request.Status.ShouldBe(CssdRequestStatus.Issued);
        Lines(request).ShouldBe([(major, 2, 2), (dressing, 1, 1)], ignoreOrder: true);

        // A request issued in full takes no more packs, and the pack stays on the shelf.
        await RefusedAsync(cssd, new IssuePacksRequest(theatre, request.Id, null, [majors[2]]), HttpStatusCode.BadRequest, "open request from the same department");
        (await PackStatusAsync(majors[2])).ShouldBe(PackStatus.Available);
    }

    [Fact]
    public async Task A_pack_more_than_a_line_asks_for_is_issued_to_the_department_but_not_counted()
    {
        var (nurse, cssd) = await SignInAsync();
        var (major, _) = await SteamSetsAsync(cssd);
        var (theatre, _) = await DepartmentsAsync(cssd);
        var majors = await ReleasedPacksAsync(cssd, major, 2);
        var request = await nurse.PostAsync<CssdRequestDto>(Requests,
            new CreateCssdRequestRequest(theatre, null, CssdPriority.Urgent, null, [new RequestLineInput(major, 1)]));

        var issued = await IssueAsync(cssd, theatre, request.Id, majors[0], majors[1]);

        issued.Select(i => i.PackId).ShouldBe(majors, ignoreOrder: true);
        issued.ShouldAllBe(i => i.DepartmentId == theatre && i.Status == SetIssueStatus.Issued);
        request = await RequestAsync(nurse, theatre, request.Id);
        request.Status.ShouldBe(CssdRequestStatus.Issued);
        Lines(request).ShouldBe([(major, 1, 1)]);
    }

    [Fact]
    public async Task An_open_or_part_issued_request_can_be_cancelled_but_not_an_issued_or_cancelled_one()
    {
        var (nurse, cssd) = await SignInAsync();
        var (major, _) = await SteamSetsAsync(cssd);
        var (theatre, _) = await DepartmentsAsync(cssd);
        var majors = await ReleasedPacksAsync(cssd, major, 4);
        var two = new[] { new RequestLineInput(major, 2) };

        // Open: cancelled with the reason; not cancelled twice.
        var open = await nurse.PostAsync<CssdRequestDto>(Requests, new CreateCssdRequestRequest(theatre, null, CssdPriority.Routine, null, two));
        await nurse.PostAsync($"{Requests}/{open.Id}/cancel", new CancelCssdRequestRequest("Surgery postponed"));
        open = await RequestAsync(nurse, theatre, open.Id);
        open.Status.ShouldBe(CssdRequestStatus.Cancelled);
        open.CancelReason.ShouldBe("Surgery postponed");
        await CancelRefusedAsync(nurse, open.Id);

        // Part issued: the rest is cancelled, and the cancelled request takes no more packs.
        var partIssued = await nurse.PostAsync<CssdRequestDto>(Requests, new CreateCssdRequestRequest(theatre, null, CssdPriority.Routine, null, two));
        await IssueAsync(cssd, theatre, partIssued.Id, majors[0]);
        await nurse.PostAsync($"{Requests}/{partIssued.Id}/cancel", new CancelCssdRequestRequest("Second tray not needed"));
        partIssued = await RequestAsync(nurse, theatre, partIssued.Id);
        partIssued.Status.ShouldBe(CssdRequestStatus.Cancelled);
        Lines(partIssued).ShouldBe([(major, 2, 1)]);
        await RefusedAsync(cssd, new IssuePacksRequest(theatre, partIssued.Id, null, [majors[1]]), HttpStatusCode.BadRequest, "open request from the same department");

        // Issued in full: it stays as it is.
        var issued = await nurse.PostAsync<CssdRequestDto>(Requests, new CreateCssdRequestRequest(theatre, null, CssdPriority.Routine, null, two));
        await IssueAsync(cssd, theatre, issued.Id, majors[2], majors[3]);
        await CancelRefusedAsync(nurse, issued.Id);
        issued = await RequestAsync(nurse, theatre, issued.Id);
        issued.Status.ShouldBe(CssdRequestStatus.Issued);
        issued.CancelReason.ShouldBeNull();
    }

    [Fact]
    public async Task Only_sterile_packs_within_their_expiry_are_issued_and_a_refused_pack_refuses_the_whole_issue()
    {
        var (nurse, cssd) = await SignInAsync();
        var (major, _) = await SteamSetsAsync(cssd);
        var (theatre, _) = await DepartmentsAsync(cssd);
        var sterile = (await ReleasedPacksAsync(cssd, major, 1))[0];
        var expired = (await ReleasedPacksAsync(cssd, major, 1))[0];
        var unreleased = (await cssd.PostAsync<LoadDto>(Loads, SteamLoad(major, 1))).Packs.Single().Id;

        // The expired pack's sterility ran out two days ago.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var twoDaysAgo = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
            (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SterilePack>().IgnoreQueryFilters()
                .Where(p => p.Id == expired)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.SterileUntil, twoDaysAgo), Ct)).ShouldBe(1);
        }

        var request = await nurse.PostAsync<CssdRequestDto>(Requests,
            new CreateCssdRequestRequest(theatre, null, CssdPriority.Routine, null, [new RequestLineInput(major, 2)]));

        // A sterile pack scanned with an expired one, or with one from a load not yet released: nothing is issued.
        await RefusedAsync(cssd, new IssuePacksRequest(theatre, request.Id, null, [sterile, expired]), HttpStatusCode.UnprocessableEntity, "cssd.pack_expired");
        await RefusedAsync(cssd, new IssuePacksRequest(theatre, request.Id, null, [sterile, unreleased]), HttpStatusCode.UnprocessableEntity, "cssd.pack_unavailable");
        (await PackStatusAsync(sterile)).ShouldBe(PackStatus.Available);
        request = await RequestAsync(nurse, theatre, request.Id);
        request.Status.ShouldBe(CssdRequestStatus.Open);
        Lines(request).ShouldBe([(major, 2, 0)]);

        // The sterile pack alone is issued.
        await IssueAsync(cssd, theatre, request.Id, sterile);
        (await RequestAsync(nurse, theatre, request.Id)).Status.ShouldBe(CssdRequestStatus.PartiallyIssued);
        (await PackStatusAsync(sterile)).ShouldBe(PackStatus.Issued);
    }

    /// <summary>A ward nurse, who requests sets for her department, and the CSSD technician, who issues them.</summary>
    private async Task<(ApiClient Nurse, ApiClient Cssd)> SignInAsync() =>
        (await server.SignInAsync(HospitalServer.Users.Nurse), await server.SignInAsync(HospitalServer.Users.CssdTechnician));

    private static IEnumerable<(Guid Set, int Asked, int Issued)> Lines(CssdRequestDto request) =>
        request.Lines.Select(l => (l.InstrumentSetId, l.Quantity, l.IssuedQuantity));

    /// <summary>The request as the department's list shows it (there is no read of one request).</summary>
    private static async Task<CssdRequestDto> RequestAsync(ApiClient api, Guid departmentId, Guid requestId) =>
        (await api.GetAsync<List<CssdRequestDto>>($"{Requests}?departmentId={departmentId}")).Single(r => r.Id == requestId);

    private static Task<List<SetIssueDto>> IssueAsync(ApiClient cssd, Guid departmentId, Guid requestId, params Guid[] packs) =>
        cssd.PostAsync<List<SetIssueDto>>(Issues, new IssuePacksRequest(departmentId, requestId, "Staff nurse", packs));

    private static async Task RefusedAsync(ApiClient cssd, IssuePacksRequest issue, HttpStatusCode status, string reason)
    {
        using var refused = await cssd.SendRawAsync(HttpMethod.Post, Issues, issue);
        refused.StatusCode.ShouldBe(status);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(reason);
    }

    private static async Task CancelRefusedAsync(ApiClient nurse, Guid requestId)
    {
        using var refused = await nurse.SendRawAsync(HttpMethod.Post, $"{Requests}/{requestId}/cancel", new CancelCssdRequestRequest("Not needed any more"));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("cssd.request_closed");
    }

    private async Task<PackStatus> PackStatusAsync(Guid packId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SterilePack>().IgnoreQueryFilters()
            .Where(p => p.Id == packId).Select(p => p.Status).SingleAsync(Ct);
    }

    /// <summary>Packs of a set from a steam load that ran within the validated parameters and was released to the shelf.</summary>
    private static async Task<List<Guid>> ReleasedPacksAsync(ApiClient cssd, Guid setId, int count)
    {
        var load = await cssd.PostAsync<LoadDto>(Loads, SteamLoad(setId, count));
        await cssd.PostAsync<LoadDto>($"{Loads}/{load.Id}/start");
        await cssd.PostAsync<LoadDto>($"{Loads}/{load.Id}/complete", new CompleteLoadRequest(134m, 210m, 4, IndicatorResult.Pass));
        load = await cssd.PostAsync<LoadDto>($"{Loads}/{load.Id}/release");
        load.Packs.ShouldAllBe(p => p.Status == PackStatus.Available);
        return load.Packs.Select(p => p.Id).ToList();
    }

    private static CreateLoadRequest SteamLoad(Guid setId, int packs) =>
        new("Autoclave 2", SterilizationMethod.Steam, "134 °C instruments", false, null, null, [new LoadPackInput(setId, packs)]);

    /// <summary>Two of the seeded steam sets: the major surgical tray and the dressing set.</summary>
    private static async Task<(Guid Major, Guid Dressing)> SteamSetsAsync(ApiClient cssd)
    {
        var sets = await cssd.GetAsync<List<InstrumentSetDto>>("/api/v1/cssd/sets");
        return (sets.Single(s => s.Code == "MAJOR-TRAY").Id, sets.Single(s => s.Code == "DRESSING-SET").Id);
    }

    /// <summary>Two of the facility's departments, as the request screens offer them.</summary>
    private static async Task<(Guid First, Guid Second)> DepartmentsAsync(ApiClient api)
    {
        var departments = await api.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments");
        return (departments[0].Id, departments[1].Id);
    }

    private sealed record LookupItem(Guid Id, string Code, string Name);
}
