using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The proof of work the portal's sign-in asks for (SRS SEC-06), over the real API: no code is sent without a solved
/// challenge, a challenge is spent by one request, a changed one is refused, every refusal gets the same answer and is
/// audited, the work asked for a number goes up after failures, and the hospital can turn the check off.
/// </summary>
public sealed class AntiAutomationTests(HospitalServer server)
{
    private const string OtpRequestPath = "/api/v1/auth/otp/request";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_code_is_sent_without_a_proof_of_work()
    {
        var phone = NewMobile();
        var portal = server.AnonymousWithoutProof();

        using var response = await portal.SendRawAsync(HttpMethod.Post, OtpRequestPath, new OtpRequest(phone));

        await ShouldBeTheGenericRefusalAsync(response);
        (await ChallengesForAsync(phone)).ShouldBe(0);
        (await RejectionsAuditedAsync()).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_solved_challenge_sends_a_code_once_and_cannot_be_used_again()
    {
        var phone = NewMobile();
        var portal = server.AnonymousWithoutProof();
        var proof = await SolvedProofAsync(portal, phone);

        using (var first = await SendWithProofAsync(portal, new OtpRequest(phone), proof))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var replay = await SendWithProofAsync(portal, new OtpRequest(phone), proof))
        {
            await ShouldBeTheGenericRefusalAsync(replay);
        }

        (await ChallengesForAsync(phone)).ShouldBe(1);
    }

    [Fact]
    public async Task A_changed_challenge_gets_the_same_answer_as_any_other_refusal()
    {
        var phone = NewMobile();
        var portal = server.AnonymousWithoutProof();
        var challenge = await ChallengeAsync(portal, phone);
        var parts = challenge.Challenge!.Split('.');
        parts[2] = "0";
        var easier = string.Join('.', parts) + ":0";

        using var response = await SendWithProofAsync(portal, new OtpRequest(phone), easier);

        await ShouldBeTheGenericRefusalAsync(response);
        (await ChallengesForAsync(phone)).ShouldBe(0);
    }

    [Fact]
    public async Task Failures_for_a_number_ask_more_work_for_it_and_an_easier_challenge_is_refused()
    {
        var phone = NewMobile();
        var portal = server.AnonymousWithoutProof();
        var early = await ChallengeAsync(portal, phone);

        // Three refused requests for the number, from elsewhere (bad proofs from a script).
        for (var i = 0; i < 3; i++)
        {
            var script = server.AnonymousFrom($"10.252.0.{i + 1}");
            script.Http.BaseAddress = server.PortalAddress;
            using var bad = await SendWithProofAsync(script, new OtpRequest(phone), "junk");
            bad.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var tooEasy = await SendWithProofAsync(portal, new OtpRequest(phone), ProofOfWork.Solve(early.Challenge!, early.Bits)))
        {
            await ShouldBeTheGenericRefusalAsync(tooEasy);
        }

        var harder = await ChallengeAsync(portal, phone);
        harder.Bits.ShouldBe(early.Bits + AutomationSuspicion.BitsPerStep);
        using (var accepted = await SendWithProofAsync(portal, new OtpRequest(phone), ProofOfWork.Solve(harder.Challenge!, harder.Bits)))
        {
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_proof_for_signing_in_is_also_asked_when_the_code_is_given()
    {
        var phone = NewMobile();
        var portal = server.AnonymousWithoutProof();
        OtpChallengeResponse sent;
        using (var requested = await SendWithProofAsync(portal, new OtpRequest(phone), await SolvedProofAsync(portal, phone)))
        {
            sent = await ApiClient.ReadAsync<OtpChallengeResponse>(requested, HttpMethod.Post, OtpRequestPath);
        }

        var code = await server.PortalCodeAsync(phone);
        using (var without = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/verify", new OtpVerifyRequest(sent.ChallengeId, code)))
        {
            await ShouldBeTheGenericRefusalAsync(without);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/otp/verify")
        {
            Content = JsonContent.Create(new OtpVerifyRequest(sent.ChallengeId, code), options: ApiClient.Json),
        };
        request.Headers.Add(AntiAutomation.ProofHeader, await SolvedProofAsync(portal, null));
        using var verified = await portal.Http.SendAsync(request, Ct);
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Turned_off_no_proof_is_asked_for()
    {
        var phone = NewMobile();
        var portal = server.AnonymousWithoutProof();
        await SetEnabledAsync(false);
        try
        {
            var challenge = await ChallengeAsync(portal, phone);
            challenge.Required.ShouldBeFalse();
            using var response = await portal.SendRawAsync(HttpMethod.Post, OtpRequestPath, new OtpRequest(phone));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await SetEnabledAsync(true);
        }
    }

    private static async Task ShouldBeTheGenericRefusalAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe(AntiAutomationEndpoints.FailedCode);
        problem.GetProperty("detail").GetString().ShouldBe("We could not complete this request. Please try again.");
    }

    private static Task<AntiAutomationChallenge> ChallengeAsync(ApiClient client, string? phone) =>
        client.PostAsync<AntiAutomationChallenge>("/api/v1/public/challenge", new AntiAutomationChallengeRequest(AntiAutomationActions.Otp, phone));

    private static async Task<string> SolvedProofAsync(ApiClient client, string? phone)
    {
        var challenge = await ChallengeAsync(client, phone);
        return ProofOfWork.Solve(challenge.Challenge!, challenge.Bits);
    }

    private static Task<HttpResponseMessage> SendWithProofAsync(ApiClient client, OtpRequest body, string proof)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, OtpRequestPath) { Content = JsonContent.Create(body, options: ApiClient.Json) };
        request.Headers.Add(AntiAutomation.ProofHeader, proof);
        return client.Http.SendAsync(request, Ct);
    }

    private async Task<int> ChallengesForAsync(string phone)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var normalized = Phone.Normalize(phone);
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuthChallenge>().CountAsync(c => c.Phone == normalized, Ct);
    }

    private async Task<List<AuditEvent>> RejectionsAuditedAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == "security.anti_automation_rejected" && e.Category == AuditCategories.Security)
            .ToListAsync(Ct);
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(AntiAutomationSettings.Enabled, enabled, cancellationToken: Ct);
    }

    private static string NewMobile() => "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
