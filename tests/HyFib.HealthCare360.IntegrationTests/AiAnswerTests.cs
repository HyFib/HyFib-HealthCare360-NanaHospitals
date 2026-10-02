using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What the AI endpoints answer (transcripts, SOAP drafts, interpreted commands) is never kept, yet a retry is still
/// recognised: a request repeated with the same Idempotency-Key does not run again (no second AI call or quota charge)
/// and answers 409 <c>idempotency.answer_not_kept</c>.
/// </summary>
public sealed class AiAnswerTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_retried_assistant_command_is_not_run_again_and_its_answer_is_not_kept()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var key = Guid.NewGuid().ToString("N");

        using (var first = await SendAsync(doctor, key))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK, "The assistant understands everyday commands without an AI provider.");
        }

        using (var retry = await SendAsync(doctor, key))
        {
            retry.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await retry.Content.ReadAsStringAsync(Ct)).ShouldContain("idempotency.answer_not_kept");
        }

        await using var scope = server.Services.CreateAsyncScope();
        var record = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<IdempotencyRecord>().AsNoTracking()
            .SingleAsync(r => r.Key == key, Ct);
        record.State.ShouldBe(IdempotencyState.Completed);
        record.ResponseBody.ShouldBeNull("The answer itself is never stored.");
    }

    private static Task<HttpResponseMessage> SendAsync(ApiClient client, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ai/assistant/command")
        {
            Content = JsonContent.Create(new { text = "go to billing", context = (object?)null }, options: ApiClient.Json),
        };
        request.Headers.Add(IdempotencyMiddleware.HeaderName, key);
        return client.Http.SendAsync(request, Ct);
    }
}
