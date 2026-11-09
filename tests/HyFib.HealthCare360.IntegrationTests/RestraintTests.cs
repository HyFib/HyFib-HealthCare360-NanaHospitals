using System.Net;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Nursing;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.DischargeStays;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Restraint of an in-patient (Mental Healthcare Act 2017 s.97, NABH COP), over the real API and PostgreSQL: only a doctor
/// orders it, with the reason and the alternatives tried, for at most four hours; the nurses' checks at the interval set,
/// an overdue check and a lapsed order on the ward's list; renewal and release by a doctor; and the debrief afterwards.
/// </summary>
public sealed class RestraintTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OrderRestraintRequest Holders(int hours = 2) => new(
        RestraintType.Physical, "Soft wrist holders, both wrists", "Pulling at his central line and striking out at staff",
        "Talking down, a sitter for an hour, his son at the bedside", hours, 15);

    [Fact]
    public async Task A_doctor_orders_a_time_limited_restraint_and_a_nurse_cannot()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        var path = $"/api/v1/nursing/admissions/{stay.Id}/restraints";

        // A nurse does not order a restraint.
        (await StatusAsync(nurse, path, Holders())).ShouldBe(HttpStatusCode.Forbidden);

        // Not longer than four hours, and only with the alternatives tried.
        (await RefusalAsync(doctor, HttpMethod.Post, path, Holders(hours: 6))).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await RefusalAsync(doctor, HttpMethod.Post, path, Holders() with { AlternativesTried = "" })).Status.ShouldBe(HttpStatusCode.BadRequest);

        var ordered = await doctor.PostAsync<RestraintEpisodeDto>(path, Holders());
        (ordered.Status, ordered.CheckIntervalMinutes).ShouldBe((RestraintStatus.Active, 15));
        ordered.OrderedByName.ShouldNotBeNullOrWhiteSpace();
        (ordered.OrderedUntil - ordered.OrderedAt).ShouldBe(TimeSpan.FromHours(2));
        ordered.NextCheckDue.ShouldBe(ordered.OrderedAt.AddMinutes(15));

        // One restraint at a time: renew it, or release first.
        (await RefusalAsync(doctor, HttpMethod.Post, path, Holders())).ShouldBe((HttpStatusCode.Conflict, "restraint.already_active"));
        (await AuditAsync(server, patient.Id, "restraint.ordered")).ShouldContain("Physical restraint ordered for 2 h");

        await ReleaseAndDischargeAsync(doctor, nurse, ordered.Id, stay.Id);
    }

    [Fact]
    public async Task Nurses_check_at_the_interval_and_the_ward_sees_an_overdue_check_and_a_lapsed_order()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (_, stay) = await AdmitAsync(server, frontOffice, doctor);
        var ordered = await doctor.PostAsync<RestraintEpisodeDto>($"/api/v1/nursing/admissions/{stay.Id}/restraints", Holders());
        var wardId = (await doctor.GetAsync<Modules.Inpatient.Features.Admissions.AdmissionDetail>($"{Admissions}/{stay.Id}")).WardId.ShouldNotBeNull();
        var ward = $"/api/v1/nursing/wards/{wardId}/restraints";
        (await nurse.GetAsync<List<WardRestraintItem>>(ward)).Single(r => r.EpisodeId == ordered.Id).CheckOverdue.ShouldBeFalse();

        // Ordered 40 minutes ago with no check since: the 15-minute check is 25 minutes late.
        await BackdateAsync(ordered.Id, TimeSpan.FromMinutes(40), lapsed: false);
        var late = (await nurse.GetAsync<List<WardRestraintItem>>(ward)).Single(r => r.EpisodeId == ordered.Id);
        (late.CheckOverdue, late.OrderLapsed).ShouldBe((true, false));
        late.MinutesOverdue.ShouldBeInRange(24, 26);

        // A concern is written down with what was done.
        var checks = $"/api/v1/nursing/restraints/{ordered.Id}/checks";
        var now = DateTimeOffset.UtcNow;
        (await RefusalAsync(nurse, HttpMethod.Post, checks, new RecordRestraintCheckRequest(now, false, true, true, true, null))).Status
            .ShouldBe(HttpStatusCode.BadRequest);
        var checkedNow = await nurse.PostAsync<RestraintEpisodeDto>(checks,
            new RecordRestraintCheckRequest(now, false, true, true, false, "Left hand cool: holder loosened, doctor told"));
        checkedNow.CheckOverdue.ShouldBeFalse();
        checkedNow.Checks.ShouldHaveSingleItem().HasConcern.ShouldBeTrue();
        (await nurse.GetAsync<List<WardRestraintItem>>(ward)).Single(r => r.EpisodeId == ordered.Id).CheckOverdue.ShouldBeFalse("checked just now");

        // The order ran out with the patient still restrained: the ward sees it.
        await BackdateAsync(ordered.Id, TimeSpan.FromMinutes(40), lapsed: true);
        (await nurse.GetAsync<List<WardRestraintItem>>(ward)).Single(r => r.EpisodeId == ordered.Id).OrderLapsed.ShouldBeTrue();

        await ReleaseAndDischargeAsync(doctor, nurse, ordered.Id, stay.Id);
    }

    [Fact]
    public async Task A_doctor_renews_or_releases_and_the_debrief_follows_the_release()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        var ordered = await doctor.PostAsync<RestraintEpisodeDto>($"/api/v1/nursing/admissions/{stay.Id}/restraints", Holders(hours: 1));
        var renew = $"/api/v1/nursing/restraints/{ordered.Id}/renew";
        var release = $"/api/v1/nursing/restraints/{ordered.Id}/release";
        var debrief = $"/api/v1/nursing/restraints/{ordered.Id}/debrief";

        // A nurse neither renews nor releases.
        (await StatusAsync(nurse, renew, new RenewRestraintRequest(2, "Still pulling at the line on review"))).ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(nurse, release, new ReleaseRestraintRequest("Calm now"))).ShouldBe(HttpStatusCode.Forbidden);

        // No debrief while restrained.
        (await RefusalAsync(nurse, HttpMethod.Post, debrief, new RestraintDebriefRequest("Pain was the trigger; analgesia before line care")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "restraint.active"));

        var renewed = await doctor.PostAsync<RestraintEpisodeDto>(renew, new RenewRestraintRequest(2, "Still pulling at the line on review at the bedside"));
        renewed.Renewals.ShouldHaveSingleItem().Reason.ShouldBe("Still pulling at the line on review at the bedside");
        renewed.OrderedUntil.ShouldBeGreaterThan(ordered.OrderedUntil);
        (await AuditAsync(server, patient.Id, "restraint.renewed")).ShouldContain("renewal 1");

        var released = await doctor.PostAsync<RestraintEpisodeDto>(release, new ReleaseRestraintRequest("Calm, line secured under a bandage"));
        released.Status.ShouldBe(RestraintStatus.Released);
        released.ReleasedByName.ShouldNotBeNullOrWhiteSpace();
        (await RefusalAsync(doctor, HttpMethod.Post, renew, new RenewRestraintRequest(1, "Agitated again an hour later")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "restraint.released"));
        (await RefusalAsync(nurse, HttpMethod.Post, $"/api/v1/nursing/restraints/{ordered.Id}/checks",
            new RecordRestraintCheckRequest(DateTimeOffset.UtcNow, true, true, true, true, null))).ShouldBe((HttpStatusCode.UnprocessableEntity, "restraint.released"));

        var debriefed = await nurse.PostAsync<RestraintEpisodeDto>(debrief, new RestraintDebriefRequest("Pain was the trigger; analgesia before line care next time"));
        debriefed.DebriefedByName.ShouldNotBeNullOrWhiteSpace();
        (await RefusalAsync(nurse, HttpMethod.Post, debrief, new RestraintDebriefRequest("Pain was the trigger, again recorded")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "restraint.debriefed"));

        // A new restraint is a new order.
        var again = await doctor.PostAsync<RestraintEpisodeDto>($"/api/v1/nursing/admissions/{stay.Id}/restraints", Holders(hours: 1));
        (await nurse.GetAsync<List<RestraintEpisodeDto>>($"/api/v1/nursing/admissions/{stay.Id}/restraints")).Select(e => e.Status)
            .ShouldBe([RestraintStatus.Active, RestraintStatus.Released]);
        await ReleaseAndDischargeAsync(doctor, nurse, again.Id, stay.Id);
    }

    /// <summary>The status of a POST refused before it reaches the endpoint (a missing permission answers with no body).</summary>
    private static async Task<HttpStatusCode> StatusAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        return response.StatusCode;
    }

    /// <summary>
    /// The order made <paramref name="ago"/> earlier, with no check since; with <paramref name="lapsed"/>, its time also
    /// run out a minute ago.
    /// </summary>
    private async Task BackdateAsync(Guid episodeId, TimeSpan ago, bool lapsed)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow;
        var orderedAt = now - ago;
        var until = lapsed ? now.AddMinutes(-1) : orderedAt.AddHours(2);
        await db.Set<RestraintEpisode>().IgnoreQueryFilters().Where(e => e.Id == episodeId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.OrderedAt, orderedAt).SetProperty(e => e.OrderedUntil, until).SetProperty(e => e.LastCheckedAt, (DateTimeOffset?)null), Ct);
    }

    /// <summary>The patient released and sent home (the hold for a signed summary is off for the run), the bed cleaned.</summary>
    private async Task ReleaseAndDischargeAsync(ApiClient doctor, ApiClient nurse, Guid episodeId, Guid admissionId)
    {
        await doctor.PostAsync<RestraintEpisodeDto>($"/api/v1/nursing/restraints/{episodeId}/release", new ReleaseRestraintRequest("Calm and cooperative"));
        await InitiateAndClearAsync(server, nurse, admissionId);
        await nurse.PostAsync<DischargeResult>(Complete(admissionId), new CompleteDischargeRequest(DischargeType.Normal, null, "Home, settled", NotifyPatient: false));
        await CleanBedAsync(nurse, admissionId);
    }
}
