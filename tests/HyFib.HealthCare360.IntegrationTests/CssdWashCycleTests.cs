using System.Net;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// CSSD-04 through the API, as the CSSD technician does it: trays go through the washer-disinfector, the cycle is
/// completed from its printout, and the sterilizer load names the wash its packs came from, so every pack traces back to
/// its wash. A load cannot name a wash that failed, one that did not wash its set, or one whose trays are all packed.
/// </summary>
public sealed class CssdWashCycleTests(HospitalServer server)
{
    private const string Washes = "/api/v1/cssd/wash-cycles";
    private const string Loads = "/api/v1/cssd/loads";

    [Fact]
    public async Task Packs_name_the_passed_wash_their_trays_came_from()
    {
        var cssd = await server.SignInAsync(HospitalServer.Users.CssdTechnician);
        var (major, dressing) = await SteamSetsAsync(cssd);

        // The trays go into the washer-disinfector; the printout shows 90 °C held for 5 minutes and they are clean.
        var wash = await cssd.PostAsync<WashCycleDto>(Washes,
            new StartWashCycleRequest("Washer-disinfector 1", "Instruments 90/5", null, null, [new WashTrayInput(major.Id, 2)]));
        wash.Status.ShouldBe(WashCycleStatus.Running);
        wash.CycleNumber.ShouldContain("CW");
        wash = await cssd.PostAsync<WashCycleDto>($"{Washes}/{wash.Id}/complete", new CompleteWashCycleRequest(90m, 5m, true));
        wash.Status.ShouldBe(WashCycleStatus.Passed);
        wash.A0.ShouldBe(3000);

        // The load dialog offers it for that set.
        var offered = await cssd.GetAsync<PagedResult<WashCycleListItem>>($"{Washes}?status=Passed&setId={major.Id}&pageSize=200");
        offered.Items.ShouldContain(w => w.Id == wash.Id && w.Trays == 2);

        // The packs made from those trays name the wash, and the wash lists them.
        var load = await cssd.PostAsync<LoadDto>(Loads, Load(new LoadPackInput(major.Id, 2, wash.Id)));
        load.Packs.Count.ShouldBe(2);
        load.Packs.ShouldAllBe(p => p.WashCycleId == wash.Id && p.WashCycleNumber == wash.CycleNumber);
        var traced = await cssd.GetAsync<WashCycleDto>($"{Washes}/{wash.Id}");
        traced.Packs.Select(p => p.PackNumber).ShouldBe(load.Packs.Select(p => p.PackNumber), ignoreOrder: true);
        traced.Packs.ShouldAllBe(p => p.LoadCycleNumber == load.CycleNumber);

        // A set the wash did not take cannot name it.
        using var mismatch = await cssd.SendRawAsync(HttpMethod.Post, Loads, Load(new LoadPackInput(dressing.Id, 1, wash.Id)));
        mismatch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await mismatch.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("cssd.wash_set_mismatch");
    }

    [Fact]
    public async Task A_wash_below_the_required_disinfection_fails_and_its_trays_cannot_be_packed()
    {
        var cssd = await server.SignInAsync(HospitalServer.Users.CssdTechnician);
        var (major, _) = await SteamSetsAsync(cssd);

        var wash = await cssd.PostAsync<WashCycleDto>(Washes,
            new StartWashCycleRequest("Washer-disinfector 2", null, null, null, [new WashTrayInput(major.Id, 1)]));
        wash = await cssd.PostAsync<WashCycleDto>($"{Washes}/{wash.Id}/complete", new CompleteWashCycleRequest(85m, 5m, true));
        wash.Status.ShouldBe(WashCycleStatus.Failed);
        wash.A0.ShouldBe(948);
        wash.FailureReason.ShouldNotBeNull().ShouldContain("A0 948");

        using var refused = await cssd.SendRawAsync(HttpMethod.Post, Loads, Load(new LoadPackInput(major.Id, 1, wash.Id)));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("cssd.wash_not_passed");
    }

    [Fact]
    public async Task A_wash_gives_as_many_packs_as_it_washed_trays_and_a_failed_load_gives_them_back()
    {
        var cssd = await server.SignInAsync(HospitalServer.Users.CssdTechnician);
        var (major, _) = await SteamSetsAsync(cssd);
        var wash = await PassedWashAsync(cssd, major.Id, trays: 2);

        // Two washed trays make two packs, never three, and none once both are packed.
        await RefusedAsync(cssd, Load(new LoadPackInput(major.Id, 3, wash.Id)), "cssd.wash_trays_used");
        var first = await cssd.PostAsync<LoadDto>(Loads, Load(new LoadPackInput(major.Id, 2, wash.Id)));
        await RefusedAsync(cssd, Load(new LoadPackInput(major.Id, 1, wash.Id)), "cssd.wash_trays_used");

        // A load that fails before release sends its packs back for reprocessing: the trays are packed again.
        await cssd.PostAsync<LoadDto>($"{Loads}/{first.Id}/abort", new FailLoadRequest("Door seal fault"));
        (await cssd.PostAsync<LoadDto>(Loads, Load(new LoadPackInput(major.Id, 2, wash.Id)))).Packs.Count.ShouldBe(2);

        // Loads sent together take turns: only one of them gets the last tray.
        var last = await PassedWashAsync(cssd, major.Id, trays: 1);
        var loads = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => cssd.SendRawAsync(HttpMethod.Post, Loads, Load(new LoadPackInput(major.Id, 1, last.Id)))));
        try
        {
            loads.Count(r => r.IsSuccessStatusCode).ShouldBe(1);
            loads.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ShouldBe(3);
        }
        finally
        {
            foreach (var response in loads)
            {
                response.Dispose();
            }
        }
    }

    private static async Task<WashCycleDto> PassedWashAsync(ApiClient cssd, Guid setId, int trays)
    {
        var wash = await cssd.PostAsync<WashCycleDto>(Washes,
            new StartWashCycleRequest("Washer-disinfector 3", null, null, null, [new WashTrayInput(setId, trays)]));
        return await cssd.PostAsync<WashCycleDto>($"{Washes}/{wash.Id}/complete", new CompleteWashCycleRequest(90m, 5m, true));
    }

    private static async Task RefusedAsync(ApiClient cssd, CreateLoadRequest load, string code)
    {
        using var refused = await cssd.SendRawAsync(HttpMethod.Post, Loads, load);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(code);
    }

    private static CreateLoadRequest Load(LoadPackInput packs) =>
        new("Autoclave 1", SterilizationMethod.Steam, "134 °C instruments", false, null, null, [packs]);

    /// <summary>Two of the seeded steam sets: the major surgical tray and the dressing set.</summary>
    private static async Task<(InstrumentSetDto Major, InstrumentSetDto Dressing)> SteamSetsAsync(ApiClient cssd)
    {
        var sets = await cssd.GetAsync<List<InstrumentSetDto>>("/api/v1/cssd/sets");
        return (sets.Single(s => s.Code == "MAJOR-TRAY"), sets.Single(s => s.Code == "DRESSING-SET"));
    }
}
