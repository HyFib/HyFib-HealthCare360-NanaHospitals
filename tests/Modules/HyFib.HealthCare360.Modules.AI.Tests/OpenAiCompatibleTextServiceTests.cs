using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.OpenAi;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace HyFib.HealthCare360.Modules.AI.Tests;

/// <summary>The OpenAI-compatible provider against a fake server: what it sends, and how it reads every kind of answer.</summary>
public sealed class OpenAiCompatibleTextServiceTests
{
    private const string Dictation = "Ravi Sharma, UHID NH26000123, mobile 9876543210. Fever for three days. Impression viral fever.";

    private static readonly AiTextSettings Settings = new(true, "openai", "local-model", "server-key", TimeSpan.FromSeconds(90),
        new Uri("https://llm.hospital.lan/v1/chat/completions"));

    private static readonly RedactionContext Patient = new(["Ravi Sharma"], ["NH26000123"]);

    private static readonly RedactedAiRequest Note = RedactedAiRequest.Redact(AiPrompts.StructureNote(Dictation, "Internal medicine"), Patient);

    private readonly StubHttpHandler _http = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OpenAiCompatibleTextService Service => new(new HttpClient(_http), NullLogger<OpenAiCompatibleTextService>.Instance);

    /// <summary>A chat completion as the servers return it.</summary>
    internal static string ChatCompletion(string? content, string finishReason = "stop", string extraMessage = "") =>
        $$"""
        {
          "id": "chatcmpl-8f2a",
          "object": "chat.completion",
          "created": 1790000000,
          "model": "served-name",
          "choices": [{ "index": 0, "message": { "role": "assistant", "content": {{JsonSerializer.Serialize(content)}}{{extraMessage}} }, "finish_reason": "{{finishReason}}" }],
          "usage": { "prompt_tokens": 812, "completion_tokens": 64, "total_tokens": 876 }
        }
        """;

    private JsonNode SentBody() => JsonNode.Parse(_http.Requests.ShouldHaveSingleItem().Body.ShouldNotBeNull())!;

    [Fact]
    public async Task Posts_the_redacted_request_to_chat_completions_with_the_bearer_key()
    {
        _http.Respond(HttpStatusCode.OK, ChatCompletion("""{"subjective":"Fever x 3 days."}"""));

        var completion = await Service.CompleteAsync(Note, Settings, Ct);

        completion.ShouldBe(new AiCompletion(true, """{"subjective":"Fever x 3 days."}""", "local-model", 812, 64, null));
        var (request, body) = _http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri.ShouldBe(new Uri("https://llm.hospital.lan/v1/chat/completions"));
        request.Headers.Authorization!.ToString().ShouldBe("Bearer server-key");
        request.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
        body.ShouldNotBeNull().ShouldNotContain("Ravi");
        body.ShouldNotContain("Sharma");
        body.ShouldNotContain("NH26000123");
        body.ShouldNotContain("9876543210");

        var sent = SentBody();
        sent["model"]!.GetValue<string>().ShouldBe("local-model");
        sent["stream"]!.GetValue<bool>().ShouldBeFalse();
        sent["max_completion_tokens"]!.GetValue<int>().ShouldBe(16_000);
        var messages = sent["messages"]!.AsArray();
        messages.Count.ShouldBe(2);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("system");
        messages[0]!["content"]!.GetValue<string>().ShouldBe(AiPrompts.NoteSystem);
        messages[1]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[1]!["content"]!.GetValue<string>().ShouldBe(Note.Request.Messages[0].Content);
        messages[1]!["content"]!.GetValue<string>().ShouldContain("[NAME_1], UHID [ID_1], mobile [PHONE_1].");
    }

    [Fact]
    public async Task Constrains_the_answer_with_the_callers_json_schema()
    {
        _http.Respond(HttpStatusCode.OK, ChatCompletion("{}"));

        await Service.CompleteAsync(Note, Settings, Ct);

        var format = SentBody()["response_format"]!;
        format["type"]!.GetValue<string>().ShouldBe("json_schema");
        format["json_schema"]!["name"]!.GetValue<string>().ShouldBe("clinical_structure-note");
        format["json_schema"]!["strict"]!.GetValue<bool>().ShouldBeTrue();
        var schema = format["json_schema"]!["schema"]!;
        schema["type"]!.GetValue<string>().ShouldBe("object");
        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldContain("suggestedIcd");
    }

    [Fact]
    public async Task Sends_no_sampling_settings_and_plain_text_without_a_schema()
    {
        _http.Respond(HttpStatusCode.OK, ChatCompletion("Hello."));
        var request = new AiRequest("other.chat", "Be brief.", [new AiMessage("user", "hello"), new AiMessage("Assistant", "hi"), new AiMessage("user", "again")], MaxTokens: 64_000);

        await Service.CompleteAsync(RedactedAiRequest.Redact(request, RedactionContext.Empty), Settings, Ct);

        var sent = SentBody().AsObject();
        sent.Select(p => p.Key).ShouldBe(["model", "messages", "max_completion_tokens", "stream"], ignoreOrder: true);
        sent["max_completion_tokens"]!.GetValue<int>().ShouldBe(32_000);
        sent["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["system", "user", "assistant", "user"]);
    }

    [Fact]
    public async Task No_api_key_means_no_authorization_header()
    {
        _http.Respond(HttpStatusCode.OK, ChatCompletion("{}"));

        await Service.CompleteAsync(Note, Settings with { ApiKey = null, Endpoint = new Uri("http://localhost:8000/v1/chat/completions") }, Ct);

        var request = _http.Requests.ShouldHaveSingleItem().Request;
        request.Headers.Authorization.ShouldBeNull();
        request.RequestUri.ShouldBe(new Uri("http://localhost:8000/v1/chat/completions"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "The AI service rejected the configured API key.")]
    [InlineData(HttpStatusCode.Forbidden, "The AI service could not process this request.")]
    [InlineData(HttpStatusCode.NotFound, "The AI server does not offer the model 'local-model' at this address.")]
    [InlineData(HttpStatusCode.BadRequest, "The AI service could not process this request.")]
    [InlineData(HttpStatusCode.TooManyRequests, "The AI service is busy. Please try again in a minute.")]
    [InlineData(HttpStatusCode.InternalServerError, "The AI service is temporarily unavailable.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "The AI service is temporarily unavailable.")]
    public async Task Turns_server_errors_into_staff_facing_messages(HttpStatusCode status, string message)
    {
        _http.Respond(status, """{"error":{"message":"Ravi Sharma's note could not be processed","type":"invalid_request_error"}}""");

        var completion = await Service.CompleteAsync(Note, Settings, Ct);

        completion.ShouldBe(new AiCompletion(false, null, "local-model", 0, 0, message));
    }

    [Theory]
    [InlineData("unreachable", "The AI service could not be reached.")]
    [InlineData("timeout", "The AI service did not respond in time.")]
    [InlineData("circuit-open", "The AI service is temporarily unavailable.")]
    public async Task Reports_a_server_it_cannot_reach_without_detail(string kind, string message)
    {
        _http.Throw(kind switch
        {
            "unreachable" => new HttpRequestException("No connection could be made because the target machine actively refused it."),
            "timeout" => new TimeoutRejectedException(),
            _ => new BrokenCircuitException(),
        });

        (await Service.CompleteAsync(Note, Settings, Ct)).Error.ShouldBe(message);
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"index":0,"finish_reason":"stop"}]}""")]
    [InlineData("""{"choices":["text"]}""")]
    public async Task An_answer_that_is_not_a_chat_completion_is_a_failure(string body)
    {
        _http.Respond(HttpStatusCode.OK, body);

        var completion = await Service.CompleteAsync(Note, Settings, Ct);

        completion.Success.ShouldBeFalse();
        completion.Text.ShouldBeNull();
        completion.Error.ShouldBe("The AI service returned an answer HC360 could not read.");
    }

    [Theory]
    [InlineData("""{"subjective":"Fever"}""", "length", "", "The AI response was cut off before it was complete.")]
    [InlineData("""{"subjective":"Fever"}""", "content_filter", "", "The AI service declined this request. Please write the note manually.")]
    [InlineData(null, "stop", """, "refusal": "I can't help with that." """, "The AI service declined this request. Please write the note manually.")]
    [InlineData(null, "stop", "", "The AI service returned an empty answer.")]
    [InlineData("  ", "stop", "", "The AI service returned an empty answer.")]
    public async Task A_refused_truncated_or_empty_answer_is_never_returned_as_complete(string? content, string finishReason, string extra, string message)
    {
        _http.Respond(HttpStatusCode.OK, ChatCompletion(content, finishReason, extra));

        var completion = await Service.CompleteAsync(Note, Settings, Ct);

        completion.ShouldBe(new AiCompletion(false, null, "local-model", 812, 64, message));
    }

    [Fact]
    public async Task Reasoning_returned_beside_the_answer_is_not_part_of_it()
    {
        _http.Respond(HttpStatusCode.OK, ChatCompletion("""{"intent":"unknown"}""", extraMessage: """, "reasoning_content": "The user said Ravi." """));

        (await Service.CompleteAsync(Note, Settings, Ct)).Text.ShouldBe("""{"intent":"unknown"}""");
    }

    [Fact]
    public async Task Missing_token_counts_are_reported_as_zero()
    {
        _http.Respond(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"{}"},"finish_reason":"stop"}]}""");

        (await Service.CompleteAsync(Note, Settings, Ct)).ShouldBe(new AiCompletion(true, "{}", "local-model", 0, 0, null));
    }

    [Fact]
    public async Task The_whole_call_ends_at_the_configured_timeout()
    {
        _http.Hang();

        var completion = await Service.CompleteAsync(Note, Settings with { Timeout = TimeSpan.FromMilliseconds(200) }, Ct);

        completion.Error.ShouldBe("The AI service did not respond in time.");
        _http.Tokens.ShouldHaveSingleItem().IsCancellationRequested.ShouldBeTrue("The server call is handed a token that ends at the deadline.");
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_reported_as_a_timeout()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _http.Hang();

        await Should.ThrowAsync<OperationCanceledException>(() => Service.CompleteAsync(Note, Settings, cancelled.Token));
    }

    [Fact]
    public async Task An_invalid_schema_fails_before_anything_is_sent()
    {
        var request = RedactedAiRequest.Redact(AiPrompts.StructureNote("fever", null) with { JsonSchema = "[1, 2]" }, RedactionContext.Empty);

        var completion = await Service.CompleteAsync(request, Settings, Ct);

        completion.ShouldBe(new AiCompletion(false, null, "local-model", 0, 0, "The AI request could not be prepared."));
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_is_sent_without_an_address()
    {
        (await Service.CompleteAsync(Note, Settings with { Endpoint = null }, Ct)).Error.ShouldBe("No AI server address is configured.");
        _http.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("clinical.structure-note", "clinical_structure-note")]
    [InlineData("assistant.command", "assistant_command")]
    [InlineData("", "answer")]
    public void Names_the_schema_as_the_servers_require(string purpose, string expected) =>
        OpenAiRequestBuilder.SchemaName(purpose).ShouldBe(expected);

    [Fact]
    public void Is_chosen_with_ai_provider_openai() => Service.Key.ShouldBe("openai");
}
