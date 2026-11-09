using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Capas;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// External quality assessment (proficiency testing) for NABL 112 and ISO 15189: a round per analyte and scheme, the
/// result the lab reported, the provider's assigned value and SD, the z-score and its band, the corrective action from the
/// Quality module's CAPA register an unacceptable round needs before it is accepted, and the lab dashboard's overdue rounds.
/// </summary>
public sealed class LabEqasTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unacceptable_round_is_judged_by_its_z_score_and_accepted_only_with_a_corrective_action_from_the_capa_register()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var quality = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var scheme = await SchemeAsync(lab);
        var creatinine = (await lab.GetAsync<PagedResult<LabTestListItem>>("/api/v1/lab/tests?search=CREA")).Items.First(t => t.Code == "CREA");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

        var roundId = await lab.PostAsync<Guid>("/api/v1/lab/eqas/rounds",
            new ScheduleEqasRoundRequest(scheme.Id, creatinine.Id, "R" + Letters(6), today.AddDays(3), today));
        var round = await RoundAsync(lab, roundId);
        (round.TestCode, round.IsOverdue, round.Performance).ShouldBe(("CREA", false, (EqasPerformance?)null));

        // The lab reports 1.62 mg/dL; the provider's assigned value is 1.20 ± 0.10: z = 4.2, unacceptable.
        round = await lab.PutAsync<EqasRoundDto>($"/api/v1/lab/eqas/rounds/{roundId}/result", new ReportEqasResultRequest(1.62m, today, round.Version));
        round = await lab.PutAsync<EqasRoundDto>($"/api/v1/lab/eqas/rounds/{roundId}/evaluation",
            new EvaluateEqasRoundRequest(1.20m, 0.10m, "Peer: Jaffe, kinetic", today, round.Version));
        (round.ZScore, round.Performance, round.NeedsCapa).ShouldBe((4.20m, EqasPerformance.Unacceptable, true));
        (await lab.GetAsync<EqasSummary>("/api/v1/lab/eqas/summary")).Attention.ShouldContain(r => r.Id == roundId, "an unacceptable round without its action is on the dashboard");

        // Not accepted without the corrective action; the quality manager raises it, the lab links it.
        (await RefusedAsync(pathologist, $"/api/v1/lab/eqas/rounds/{roundId}/accept", new AcceptEqasRoundRequest("Recalibrated", round.Version), HttpMethod.Post))
            .ShouldBe("eqas.capa_required");
        var ownerId = (await quality.GetAsync<AuthUser>("/api/v1/auth/me")).Id;
        var capaId = await quality.PostAsync<Guid>("/api/v1/quality/capas", new CreateCapaRequest(
            CapaSource.Other, null, null, CapaType.Corrective, $"EQAS {round.RoundCode} creatinine z = 4.2: recalibrate, review the calibrator lot", ownerId, today.AddDays(14)));
        (await RefusedAsync(lab, $"/api/v1/lab/eqas/rounds/{roundId}/capa", new LinkEqasCapaRequest(Guid.CreateVersion7(), round.Version), HttpMethod.Put))
            .ShouldBe("validation", "only an action from the CAPA register is linked");
        // The lab links it by the number the quality manager gives it (lab staff do not browse the CAPA register).
        var capaNumber = (await quality.GetAsync<CapaDetail>($"/api/v1/quality/capas/{capaId}")).CapaNumber;
        round = await lab.PutAsync<EqasRoundDto>($"/api/v1/lab/eqas/rounds/{roundId}/capa", new LinkEqasCapaRequest(null, round.Version, capaNumber));
        (round.NeedsCapa, round.CapaStatus, round.CapaId, round.CapaNumber).ShouldBe((false, "Open", (Guid?)capaId, capaNumber));

        round = await pathologist.PostAsync<EqasRoundDto>($"/api/v1/lab/eqas/rounds/{roundId}/accept",
            new AcceptEqasRoundRequest("Calibrator lot 2611 drifted; recalibrated and repeated", round.Version));
        round.AcceptedAt.ShouldNotBeNull();

        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var trail = (await admin.GetAsync<PagedResult<AuditEventDto>>($"/api/v1/admin/audit?entityType=EqasRound&entityId={roundId}&pageSize=50")).Items;
        trail.Select(a => a.Action).Where(a => a.StartsWith("lab.eqas.", StringComparison.Ordinal)).ShouldBe(["lab.eqas.round_scheduled", "lab.eqas.result_reported", "lab.eqas.evaluated", "lab.eqas.capa_linked", "lab.eqas.accepted"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_round_not_reported_by_its_due_date_is_overdue_on_the_lab_dashboard()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var scheme = await SchemeAsync(lab);
        var potassium = (await lab.GetAsync<PagedResult<LabTestListItem>>("/api/v1/lab/tests?search=K")).Items.First(t => t.Code == "K");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        var before = (await lab.GetAsync<EqasSummary>("/api/v1/lab/eqas/summary")).Overdue;

        var late = await lab.PostAsync<Guid>("/api/v1/lab/eqas/rounds", new ScheduleEqasRoundRequest(scheme.Id, potassium.Id, "R" + Letters(6), today.AddDays(-2), today.AddDays(-9)));

        var summary = await lab.GetAsync<EqasSummary>("/api/v1/lab/eqas/summary");
        summary.Overdue.ShouldBe(before + 1);
        summary.Attention.Single(r => r.Id == late).IsOverdue.ShouldBeTrue();
        (await lab.GetAsync<PagedResult<EqasRoundDto>>("/api/v1/lab/eqas/rounds?overdue=true&pageSize=200")).Items.ShouldContain(r => r.Id == late);

        // A panel or a text test is not proficiency-tested here; the same round twice is refused.
        var cbc = (await lab.GetAsync<PagedResult<LabTestListItem>>("/api/v1/lab/tests?search=CBC")).Items.First(t => t.IsPanel);
        (await RefusedAsync(lab, "/api/v1/lab/eqas/rounds", new ScheduleEqasRoundRequest(scheme.Id, cbc.Id, "R1", today, null), HttpMethod.Post)).ShouldBe("validation");
    }

    private static async Task<EqasSchemeDto> SchemeAsync(ApiClient lab)
    {
        var name = "Clinical chemistry " + Letters(6);
        await lab.PostAsync<JsonObject>("/api/v1/lab/eqas/schemes", new SaveEqasSchemeRequest(name, "CMC Vellore", "NANA-117", 12, true, null));
        return (await lab.GetAsync<List<EqasSchemeDto>>("/api/v1/lab/eqas/schemes")).Single(s => s.Name == name);
    }

    private static async Task<EqasRoundDto> RoundAsync(ApiClient api, Guid id) =>
        (await api.GetAsync<PagedResult<EqasRoundDto>>("/api/v1/lab/eqas/rounds?pageSize=200")).Items.Single(r => r.Id == id);

    private static async Task<string?> RefusedAsync(ApiClient client, string path, object body, HttpMethod method)
    {
        using var response = await client.SendRawAsync(method, path, body);
        response.IsSuccessStatusCode.ShouldBeFalse(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
