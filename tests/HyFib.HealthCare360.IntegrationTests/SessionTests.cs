using System.Net;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Refresh-token rotation over the real API. A retry within the grace (the response was lost on the WiFi link) keeps the
/// session; a copy of a token replayed by someone else ends it for everyone who holds its tokens.
/// </summary>
public sealed class SessionTests(HospitalServer server)
{
    [Fact]
    public async Task A_retry_after_a_lost_response_keeps_the_session()
    {
        var first = await server.SignInForRefreshCookieAsync(HospitalServer.Users.Nurse);
        using (await server.RefreshAsync(first))
        {
            // The response is lost: the client never stores the token it carried.
        }

        using var retry = await server.RefreshAsync(first);
        var reissued = HospitalServer.RefreshCookieOf(retry).ShouldNotBeNull();
        using var next = await server.RefreshAsync(reissued);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        next.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_copied_refresh_token_replayed_by_someone_else_ends_the_session_for_both()
    {
        var first = await server.SignInForRefreshCookieAsync(HospitalServer.Users.Lab);
        using var rotated = await server.RefreshAsync(first);
        var client = HospitalServer.RefreshCookieOf(rotated).ShouldNotBeNull();

        // A copy of the first token, replayed within the grace, is re-issued a token of its own...
        using var replayed = await server.RefreshAsync(first);
        var thief = HospitalServer.RefreshCookieOf(replayed).ShouldNotBeNull();

        // ...but the real client's next refresh presents the token that replay retired, which ends the session.
        using var clientRefresh = await server.RefreshAsync(client);
        using var thiefRefresh = await server.RefreshAsync(thief);

        clientRefresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        thiefRefresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
