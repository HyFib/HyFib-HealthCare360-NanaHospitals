using System.Net;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class ClaudeTextServiceTests
{
    private static readonly AiTextSettings Settings = new(true, "anthropic", "claude-opus-5", "sk-ant-test-key", TimeSpan.FromSeconds(90));

    private static readonly AiRequest Command = AiPrompts.Command("go to billing", "/", patientOpen: false, TestClock.Today);

    private static readonly RedactedAiRequest Request = RedactedAiRequest.Redact(Command, RedactionContext.Empty);

    private readonly IAnthropicClientProvider _clients = Substitute.For<IAnthropicClientProvider>();
    private readonly IAnthropicClient _client = Substitute.For<IAnthropicClient>();

    public ClaudeTextServiceTests()
    {
        _clients.GetClient(default!, default).ReturnsForAnyArgs(_client);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClaudeTextService Service => new(_clients, NullLogger<ClaudeTextService>.Instance);

    private void ApiReturns(Task<BetaMessage> result) =>
        _client.Beta.Messages.Create(Arg.Any<MessageCreateParams>(), Arg.Any<CancellationToken>()).Returns(result);

    /// <summary>The SDK's own exception types, built the way the SDK raises them.</summary>
    private static Exception Failure(string kind) => kind switch
    {
        "unauthorized" => new AnthropicUnauthorizedException(new HttpRequestException("401")) { StatusCode = HttpStatusCode.Unauthorized, ResponseBody = "{}" },
        "not-found" => new AnthropicNotFoundException(new HttpRequestException("404")) { StatusCode = HttpStatusCode.NotFound, ResponseBody = "{}" },
        "rate-limit" => new AnthropicRateLimitException(new HttpRequestException("429")) { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "{}" },
        "overloaded" => new Anthropic5xxException(new HttpRequestException("529")) { StatusCode = (HttpStatusCode)529, ResponseBody = "{}" },
        "network" => new AnthropicIOException("connection reset", new HttpRequestException("reset")),
        _ => new TaskCanceledException("timed out"),
    };

    [Fact]
    public async Task Calls_the_messages_api_with_the_configured_key_and_model()
    {
        ApiReturns(Task.FromResult(ClaudeResponseMapperTests.Message("end_turn", """[{ "type": "text", "text": "{\"intent\":\"navigate\",\"route\":\"/billing\"}" }]""")));

        var completion = await Service.CompleteAsync(Request, Settings, Ct);

        completion.Success.ShouldBeTrue();
        completion.Text.ShouldBe("""{"intent":"navigate","route":"/billing"}""");
        _clients.Received(1).GetClient("sk-ant-test-key", TimeSpan.FromSeconds(90));
        await _client.Beta.Messages.Received(1).Create(Arg.Is<MessageCreateParams>(p => p.Model.Raw() == "claude-opus-5"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_invalid_schema_fails_before_anything_is_sent()
    {
        var completion = await Service.CompleteAsync(RedactedAiRequest.Redact(Command with { JsonSchema = "not json" }, RedactionContext.Empty), Settings, Ct);

        completion.ShouldBe(new AiCompletion(false, null, "claude-opus-5", 0, 0, "The AI request could not be prepared."));
        _clients.DidNotReceiveWithAnyArgs().GetClient(default!, default);
    }

    [Fact]
    public async Task The_whole_call_retries_included_ends_at_the_configured_timeout()
    {
        CancellationToken passed = default;
        _client.Beta.Messages.Create(Arg.Any<MessageCreateParams>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                passed = call.Arg<CancellationToken>();
                return Task.Delay(Timeout.Infinite, passed).ContinueWith<BetaMessage>(_ => throw new TaskCanceledException(), TaskScheduler.Default);
            });

        var completion = await Service.CompleteAsync(Request, Settings with { Timeout = TimeSpan.FromMilliseconds(200) }, Ct);

        completion.Error.ShouldBe("The AI service did not respond in time.");
        passed.IsCancellationRequested.ShouldBeTrue("The SDK is handed a token that ends at the deadline.");
    }

    [Theory]
    [InlineData("unauthorized", "The AI service rejected the configured API key.")]
    [InlineData("not-found", "The AI model 'claude-opus-5' is not available to this account.")]
    [InlineData("rate-limit", "The AI service is busy. Please try again in a minute.")]
    [InlineData("overloaded", "The AI service is temporarily unavailable.")]
    [InlineData("network", "The AI service could not be reached. Check the internet connection.")]
    [InlineData("timeout", "The AI service did not respond in time.")]
    public async Task Turns_api_failures_into_staff_facing_messages(string kind, string message)
    {
        ApiReturns(Task.FromException<BetaMessage>(Failure(kind)));

        var completion = await Service.CompleteAsync(Request, Settings, Ct);

        completion.ShouldBe(new AiCompletion(false, null, "claude-opus-5", 0, 0, message));
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_reported_as_a_timeout()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        ApiReturns(Task.FromCanceled<BetaMessage>(cancelled.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Service.CompleteAsync(Request, Settings, cancelled.Token));
    }

    [Fact]
    public void Reuses_one_client_per_key_and_timeout()
    {
        var provider = new AnthropicClientProvider();

        var first = provider.GetClient("sk-ant-one", TimeSpan.FromSeconds(90));

        provider.GetClient("sk-ant-one", TimeSpan.FromSeconds(90)).ShouldBeSameAs(first);
        provider.GetClient("sk-ant-two", TimeSpan.FromSeconds(90)).ShouldNotBeSameAs(first);
        first.MaxRetries.ShouldBe(2);
    }
}
