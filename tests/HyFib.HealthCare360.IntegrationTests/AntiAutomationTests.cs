using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// The proof of work the portal's sign-in and feedback ask for (SRS SEC-06), over the real API: no code is sent and no
/// feedback taken without a solved challenge, a challenge is spent by one request, a changed one is refused, every
/// refusal gets the same answer and is audited, the work asked of an address or a number goes up after failures (refused
/// proofs, wrong codes, dead links, rate-limit hits) but not for a patient who was only asked for more work, and the
/// hospital can turn the check off.
/// </summary>
public sealed class AntiAutomationTests(HospitalServer server)
{
    private const string OtpRequestPath = "/api/v1/auth/otp/request";
    private const string OtpVerifyPath = "/api/v1/auth/otp/verify";

    private static readonly object Feedback = new { nps = 9 };

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
        using (var without = await portal.SendRawAsync(HttpMethod.Post, OtpVerifyPath, new OtpVerifyRequest(sent.ChallengeId, code)))
        {
            await ShouldBeTheGenericRefusalAsync(without);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, OtpVerifyPath)
        {
            Content = JsonContent.Create(new OtpVerifyRequest(sent.ChallengeId, code), options: ApiClient.Json),
        };
        request.Headers.Add(AntiAutomation.ProofHeader, await SolvedProofAsync(portal, null));
        using var verified = await portal.Http.SendAsync(request, Ct);
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_patient_whose_number_failed_before_gets_in_on_the_retry_without_raising_the_work()
    {
        var phone = NewMobile();

        // Five refused requests for the number, from elsewhere: it now needs one step more.
        for (var i = 0; i < 5; i++)
        {
            var script = server.AnonymousFrom($"10.252.2.{i + 1}");
            script.Http.BaseAddress = server.PortalAddress;
            using var bad = await SendWithProofAsync(script, new OtpRequest(phone), "junk");
            bad.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // As the portal does: a proof solved before the number was typed is too easy for it, and the retry solves one for
        // the number.
        var portal = server.AnonymousWithoutProof();
        var before = (await ChallengeAsync(portal, null)).Bits;
        var prepared = await SolvedProofAsync(portal, null);
        using (var tooEasy = await SendWithProofAsync(portal, new OtpRequest(phone), prepared))
        {
            await ShouldBeTheGenericRefusalAsync(tooEasy);
        }

        using (var retry = await SendWithProofAsync(portal, new OtpRequest(phone), await SolvedProofAsync(portal, phone)))
        {
            retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Neither the address nor the number was held to account for the too-easy proof: 5 failures, not 6.
        (await ChallengeAsync(portal, null)).Bits.ShouldBe(before);
        (await ChallengeAsync(portal, phone)).Bits.ShouldBe(before + AutomationSuspicion.BitsPerStep);
    }

    [Fact]
    public async Task Wrong_codes_raise_the_work_for_the_address_and_for_the_number()
    {
        var phone = NewMobile();
        var portal = server.AnonymousOnPortal(ownAddress: true);
        var before = (await ChallengeAsync(portal, null)).Bits;
        var sent = await portal.PostAsync<OtpChallengeResponse>(OtpRequestPath, new OtpRequest(phone));
        var code = await server.PortalCodeAsync(phone);
        var wrong = string.Concat(code.Select(digit => (char)('0' + ((digit - '0' + 1) % 10))));

        for (var i = 0; i < 3; i++)
        {
            using var refused = await portal.SendRawAsync(HttpMethod.Post, OtpVerifyPath, new OtpVerifyRequest(sent.ChallengeId, wrong));
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await ChallengeAsync(portal, null)).Bits.ShouldBe(before + AutomationSuspicion.BitsPerStep);

        // Giving a code names only its challenge, yet the failures count against the number, from any address.
        var elsewhere = server.AnonymousFrom("10.252.1.1");
        (await ChallengeAsync(elsewhere, phone)).Bits.ShouldBe(before + AutomationSuspicion.BitsPerStep);
        (await ChallengeAsync(elsewhere, null)).Bits.ShouldBe(before);
    }

    [Fact]
    public async Task Rate_limited_code_requests_raise_the_work_for_the_address()
    {
        var portal = server.AnonymousOnPortal(ownAddress: true);
        var before = (await ChallengeAsync(portal, null)).Bits;

        // Five codes per address in 15 minutes, each with a good proof; the next three are refused by the rate limit.
        for (var i = 0; i < 5; i++)
        {
            using var sent = await portal.SendRawAsync(HttpMethod.Post, OtpRequestPath, new OtpRequest(NewMobile()));
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        for (var i = 0; i < 3; i++)
        {
            using var limited = await portal.SendRawAsync(HttpMethod.Post, OtpRequestPath, new OtpRequest(NewMobile()));
            limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        }

        (await ChallengeAsync(portal, null)).Bits.ShouldBe(before + AutomationSuspicion.BitsPerStep);
    }

    [Fact]
    public async Task No_feedback_is_taken_without_a_proof_of_work()
    {
        var kiosk = server.AnonymousWithoutProof(portal: false);

        using var form = await kiosk.SendRawAsync(HttpMethod.Post, $"/api/v1/public/feedback/forms/{DeadToken()}", Feedback);
        using var invitation = await kiosk.SendRawAsync(HttpMethod.Post, $"/api/v1/public/feedback/invitations/{DeadToken()}", Feedback);

        await ShouldBeTheGenericRefusalAsync(form);
        await ShouldBeTheGenericRefusalAsync(invitation);
    }

    [Fact]
    public async Task Dead_feedback_links_raise_the_work_for_the_address()
    {
        var kiosk = server.Anonymous(ownAddress: true);
        var before = (await ChallengeAsync(kiosk, null, AntiAutomationActions.Feedback)).Bits;

        for (var i = 0; i < 3; i++)
        {
            using var dead = await kiosk.SendRawAsync(HttpMethod.Post, $"/api/v1/public/feedback/invitations/{DeadToken()}", Feedback);
            dead.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await ChallengeAsync(kiosk, null, AntiAutomationActions.Feedback)).Bits.ShouldBe(before + AutomationSuspicion.BitsPerStep);
    }

    [Fact]
    public async Task Feedback_filled_in_wrongly_neither_spends_its_proof_nor_counts_as_a_failure()
    {
        var kiosk = server.AnonymousWithoutProof(portal: false);
        var before = (await ChallengeAsync(kiosk, null, AntiAutomationActions.Feedback)).Bits;
        var challenge = await ChallengeAsync(kiosk, null, AntiAutomationActions.Feedback);
        var proof = ProofOfWork.Solve(challenge.Challenge!, challenge.Bits);
        var path = $"/api/v1/public/feedback/forms/{DeadToken()}";

        for (var i = 0; i < 3; i++)
        {
            using var invalid = await SendWithProofAsync(kiosk, path, new { nps = 42 }, proof);
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await ChallengeAsync(kiosk, null, AntiAutomationActions.Feedback)).Bits.ShouldBe(before);

        // The proof is still good: the corrected form gets past the check (to a link that does not exist).
        using var corrected = await SendWithProofAsync(kiosk, path, Feedback, proof);
        corrected.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_number_that_is_not_a_mobile_number_neither_spends_its_proof_nor_counts_as_a_failure()
    {
        var portal = server.AnonymousWithoutProof();
        var before = (await ChallengeAsync(portal, null)).Bits;
        var proof = await SolvedProofAsync(portal, null);

        foreach (var phone in new[] { "12345", "", "not a number" })
        {
            using var invalid = await SendWithProofAsync(portal, new OtpRequest(phone), proof);
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("phone")[0].GetString()
                .ShouldBe("Enter a valid 10-digit mobile number.");
        }

        (await ChallengeAsync(portal, null)).Bits.ShouldBe(before);

        // The proof is still good: the corrected number gets its code.
        using var corrected = await SendWithProofAsync(portal, new OtpRequest(NewMobile()), proof);
        corrected.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_work_cannot_be_set_beyond_what_a_phone_does_within_a_challenges_life()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        try
        {
            using (var tooMuch = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/settings/{AntiAutomationSettings.MaxBits}",
                new SaveSettingRequest(JsonValue.Create(22), null, false, null)))
            {
                tooMuch.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                (await tooMuch.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("value")[0].GetString()
                    .ShouldBe("Enter a whole number of bits from 0 to 20.");
            }

            using (var allowed = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/settings/{AntiAutomationSettings.MaxBits}",
                new SaveSettingRequest(JsonValue.Create(20), null, false, null, await admin.SettingVersionAsync(AntiAutomationSettings.MaxBits))))
            {
                allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(AntiAutomationSettings.MaxBits, null, Ct);
        }
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

    private static Task<AntiAutomationChallenge> ChallengeAsync(ApiClient client, string? phone, string action = AntiAutomationActions.Otp) =>
        client.PostAsync<AntiAutomationChallenge>("/api/v1/public/challenge", new AntiAutomationChallengeRequest(action, phone));

    private static async Task<string> SolvedProofAsync(ApiClient client, string? phone)
    {
        var challenge = await ChallengeAsync(client, phone);
        return ProofOfWork.Solve(challenge.Challenge!, challenge.Bits);
    }

    private static Task<HttpResponseMessage> SendWithProofAsync(ApiClient client, OtpRequest body, string proof) =>
        SendWithProofAsync(client, OtpRequestPath, body, proof);

    private static Task<HttpResponseMessage> SendWithProofAsync(ApiClient client, string path, object body, string proof)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, body.GetType(), options: ApiClient.Json) };
        request.Headers.Add(AntiAutomation.ProofHeader, proof);
        return client.Http.SendAsync(request, Ct);
    }

    /// <summary>A well-formed feedback token that no form or link has.</summary>
    private static string DeadToken() => "dead" + Guid.NewGuid().ToString("N");

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
