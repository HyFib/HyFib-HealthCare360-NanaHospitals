using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Platform.Integrations;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace HyFib.HealthCare360.Integrations.Tests.Common;

/// <summary>
/// A call that got no answer is told from one the provider answered, refusals included: the outbox lets a provider that
/// does not answer cool down, and keeps trying one that refuses a message.
/// </summary>
public sealed class IntegrationErrorsTests
{
    [Fact]
    public void Timeouts_an_open_circuit_and_connections_that_fail_got_no_answer()
    {
        Exception[] noAnswer =
        [
            new TimeoutRejectedException(),
            new BrokenCircuitException(),
            new TaskCanceledException("The connection attempt timed out.", new TimeoutException()),
            new TimeoutException(),
            new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"),
            new HttpRequestException("No such host is known."),
        ];

        noAnswer.ShouldAllBe(failure => IntegrationErrors.IsUnreachable(failure));
        noAnswer.Select(failure => IntegrationErrors.Failed(failure, "No answer").ProviderUnreachable).ShouldAllBe(unreachable => unreachable);
    }

    [Fact]
    public void An_http_error_or_an_unreadable_answer_is_an_answer()
    {
        Exception[] answered =
        [
            new HttpRequestException("Bad request", null, HttpStatusCode.BadRequest),
            new HttpRequestException("Service unavailable", null, HttpStatusCode.ServiceUnavailable),
            new JsonException("Unexpected character"),
            new IntegrationException("The gateway refused the order."),
        ];

        answered.ShouldAllBe(failure => !IntegrationErrors.IsUnreachable(failure));
        IntegrationErrors.Failed(answered[0], "Refused").ShouldBe(SendResult.Fail("Refused"));
    }
}
