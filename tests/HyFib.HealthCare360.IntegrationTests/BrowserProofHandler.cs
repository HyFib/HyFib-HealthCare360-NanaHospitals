using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Does what the portal does before a request the server asks a proof of work for (SRS SEC-06): asks for a challenge,
/// with the mobile number when the request has one, solves it and sends the proof; and when the proof is refused (the
/// work asked for went up in the meantime), asks once more and tries again. A request that already carries a proof is
/// sent as it is, so a test can send a bad one.
/// </summary>
public sealed class BrowserProofHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private static readonly (string Path, string Action)[] Protected =
    [
        ("/api/v1/auth/otp/", AntiAutomationActions.Otp),
        ("/api/v1/public/feedback/forms/", AntiAutomationActions.Feedback),
        ("/api/v1/public/feedback/invitations/", AntiAutomationActions.Feedback),
    ];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var action = request.Method == HttpMethod.Post && request.RequestUri is { } uri
            ? Protected.FirstOrDefault(p => uri.AbsolutePath.StartsWith(p.Path, StringComparison.Ordinal)).Action
            : null;
        if (action is null || request.Headers.Contains(AntiAutomation.ProofHeader))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var subject = request.RequestUri!.AbsolutePath.EndsWith("/otp/request", StringComparison.Ordinal) ? PhoneOf(body) : null;
        for (var attempt = 1; ; attempt++)
        {
            using var challengeRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(request.RequestUri, "/api/v1/public/challenge"))
            {
                Content = JsonContent.Create(new AntiAutomationChallengeRequest(action, subject), options: ApiClient.Json),
            };
            foreach (var header in request.Headers.Where(h => h.Key.StartsWith("X-", StringComparison.Ordinal)))
            {
                challengeRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var challengeResponse = await base.SendAsync(challengeRequest, cancellationToken);
            var challenge = await ApiClient.ReadAsync<AntiAutomationChallenge>(challengeResponse, HttpMethod.Post, "/api/v1/public/challenge");

            using var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (body is not null)
            {
                copy.Content = new StringContent(body, Encoding.UTF8, request.Content!.Headers.ContentType?.MediaType ?? "application/json");
            }

            if (challenge.Required)
            {
                copy.Headers.TryAddWithoutValidation(AntiAutomation.ProofHeader, ProofOfWork.Solve(challenge.Challenge!, challenge.Bits));
            }

            var response = await base.SendAsync(copy, cancellationToken);
            if (attempt >= 2 || response.StatusCode != HttpStatusCode.Forbidden || !await IsCheckFailedAsync(response, cancellationToken))
            {
                return response;
            }

            response.Dispose();
        }
    }

    private static string? PhoneOf(string? body)
    {
        try
        {
            return body is null ? null : JsonDocument.Parse(body).RootElement.TryGetProperty("phone", out var phone) ? phone.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> IsCheckFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.LoadIntoBufferAsync(ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Contains(AntiAutomationEndpoints.FailedCode, StringComparison.Ordinal);
    }
}
