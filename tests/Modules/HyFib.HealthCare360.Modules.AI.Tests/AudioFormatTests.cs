using System.Net;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Speech;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class AudioFormatTests
{
    private static readonly byte[] Webm = [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81];
    private static readonly byte[] Wav = [.. "RIFF"u8, 0x24, 0x08, 0x00, 0x00, .. "WAVE"u8, .. "fmt "u8];
    private static readonly byte[] Ogg = [.. "OggS"u8, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static readonly SpeechConfig Whisper = new(
        new SpeechSettings { Provider = "whisper", Url = "https://stt.lan:8000/v1/audio/transcriptions" },
        new Uri("https://stt.lan:8000/v1/audio/transcriptions"),
        "stt-key",
        AiEnabled: true);

    private readonly StubHttpHandler _http = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WhisperSpeechToText Transcriber => new(new HttpClient(_http), NullLogger<WhisperSpeechToText>.Instance);

    [Theory]
    [InlineData("audio/webm;codecs=opus", "audio/webm")]
    [InlineData("video/webm", "audio/webm")]
    [InlineData("audio/x-wav", "audio/wav")]
    [InlineData("AUDIO/OGG", "audio/ogg")]
    [InlineData("audio/mpeg", null)]
    [InlineData("application/octet-stream", null)]
    [InlineData(null, null)]
    public void Accepts_only_dictation_audio_formats(string? contentType, string? expected) =>
        AudioFormat.Normalize(contentType).ShouldBe(expected);

    [Fact]
    public void The_file_signature_must_match_the_declared_type()
    {
        AudioFormat.MatchesSignature("audio/webm", Webm).ShouldBeTrue();
        AudioFormat.MatchesSignature("audio/wav", Wav).ShouldBeTrue();
        AudioFormat.MatchesSignature("audio/ogg", Ogg).ShouldBeTrue();
        AudioFormat.MatchesSignature("audio/webm", Wav).ShouldBeFalse();
        AudioFormat.MatchesSignature("audio/wav", "RIFF"u8.ToArray()).ShouldBeFalse();
        AudioFormat.MatchesSignature("audio/ogg", "MZ\u0090\u0000"u8.ToArray()).ShouldBeFalse();
    }

    [Theory]
    [InlineData("audio/webm", ".webm")]
    [InlineData("audio/wav", ".wav")]
    [InlineData("audio/ogg", ".ogg")]
    public void Names_the_upload_by_type(string mediaType, string extension) => AudioFormat.Extension(mediaType).ShouldBe(extension);

    [Fact]
    public async Task Sends_the_audio_to_the_whisper_endpoint_and_returns_the_text()
    {
        _http.Respond(HttpStatusCode.OK, """{"text":"  Patient has fever for three days.  "}""");

        var text = await Transcriber.TranscribeAsync(new MemoryStream(Webm), "audio/webm", "en", Whisper, Ct);

        text.ShouldBe("Patient has fever for three days.");
        var (request, body) = _http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri.ShouldBe(new Uri("https://stt.lan:8000/v1/audio/transcriptions"));
        request.Headers.Authorization!.ToString().ShouldBe("Bearer stt-key");
        body.ShouldNotBeNull().ShouldContain("dictation.webm");
        body.ShouldContain("whisper-1");
        body.ShouldContain("name=response_format");
    }

    [Fact]
    public async Task No_api_key_means_no_authorization_header()
    {
        _http.Respond(HttpStatusCode.OK, """{"text":"ok"}""");

        await Transcriber.TranscribeAsync(new MemoryStream(Wav), "audio/wav", "hi", Whisper with { ApiKey = null }, Ct);

        _http.Requests[0].Request.Headers.Authorization.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "The speech server returned HTTP 500.")]
    [InlineData(HttpStatusCode.OK, """{"segments":[]}""", "The speech server returned an unexpected response.")]
    [InlineData(HttpStatusCode.OK, "<html>", "The speech server could not be reached.")]
    public async Task Reports_speech_server_problems_in_plain_words(HttpStatusCode status, string body, string message)
    {
        _http.Respond(status, body);

        var failure = await Should.ThrowAsync<SpeechToTextException>(() => Transcriber.TranscribeAsync(new MemoryStream(Webm), "audio/webm", "en", Whisper, Ct));

        failure.Message.ShouldBe(message);
    }

    [Fact]
    public async Task An_unreachable_server_is_reported_without_detail()
    {
        _http.Throw(new HttpRequestException("No connection could be made because the target machine actively refused it."));

        var failure = await Should.ThrowAsync<SpeechToTextException>(() => Transcriber.TranscribeAsync(new MemoryStream(Webm), "audio/webm", "en", Whisper, Ct));

        failure.Message.ShouldBe("The speech server could not be reached.");
    }

    [Fact]
    public async Task The_router_explains_that_desktop_dictation_is_used_when_no_server_is_configured()
    {
        var router = Router(speech: null);

        router.IsEnabled.ShouldBeFalse();
        var failure = await Should.ThrowAsync<SpeechToTextException>(() => router.TranscribeAsync(new MemoryStream(Webm), "audio/webm", "en", Ct));
        failure.Message.ShouldBe("Server speech-to-text is not configured; HC360 Desktop dictates with offline Windows speech.");
    }

    [Fact]
    public async Task The_router_refuses_unsupported_audio_and_uses_the_configured_language()
    {
        _http.Respond(HttpStatusCode.OK, """{"text":"ok"}""");
        var router = Router(new SpeechSettings { Provider = "Whisper", Url = "https://stt.lan:8000/v1/audio/transcriptions", Language = "ta" });

        await Should.ThrowAsync<SpeechToTextException>(() => router.TranscribeAsync(new MemoryStream(Webm), "audio/mpeg", "en", Ct));
        (await router.TranscribeAsync(new MemoryStream(Webm), "audio/webm;codecs=opus", "", Ct)).ShouldBe("ok");

        _http.Requests.ShouldHaveSingleItem().Body.ShouldNotBeNull().ShouldContain("\r\n\r\nta\r\n");
    }

    [Fact]
    public async Task The_router_sends_no_audio_while_ai_is_off()
    {
        var router = Router(new SpeechSettings { Provider = "whisper", Url = "https://stt.lan:8000/v1/audio/transcriptions" }, aiEnabled: false);

        router.IsEnabled.ShouldBeFalse();
        var failure = await Should.ThrowAsync<SpeechToTextException>(() => router.TranscribeAsync(new MemoryStream(Webm), "audio/webm", "en", Ct));
        failure.Message.ShouldBe("AI features are turned off for this facility (setting ai.enabled).");
        _http.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://stt.lan:8000/v1/audio/transcriptions")]
    [InlineData("http://10.0.0.5:8000/v1/audio/transcriptions")]
    [InlineData("ftp://stt.lan/v1/audio/transcriptions")]
    [InlineData("https://user:secret@stt.lan/v1/audio/transcriptions")]
    [InlineData("stt.lan/v1/audio/transcriptions")]
    public async Task The_router_sends_no_audio_to_an_address_that_is_not_https(string url)
    {
        var router = Router(new SpeechSettings { Provider = "whisper", Url = url });

        router.IsEnabled.ShouldBeFalse();
        var failure = await Should.ThrowAsync<SpeechToTextException>(() => router.TranscribeAsync(new MemoryStream(Webm), "audio/webm", "en", Ct));
        failure.Message.ShouldStartWith("The speech-to-text URL (ai.speech.url) must be an https address");
        _http.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://stt.lan:8000/v1/audio/transcriptions")]
    [InlineData("http://localhost:8000/v1/audio/transcriptions")]
    [InlineData("http://127.0.0.1:8000/v1/audio/transcriptions")]
    public void Https_or_plain_http_to_this_machine_is_accepted(string url) =>
        AiEndpoint.TryCreate(url).ShouldBe(new Uri(url));

    private SpeechToTextRouter Router(SpeechSettings? speech, bool aiEnabled = true)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool?>(AiSettingKeys.Enabled, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(aiEnabled);
        settings.GetAsync<SpeechSettings>(AiSettingKeys.Speech, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(speech);
        settings.GetAsync<JsonElement>(AiSettingKeys.SpeechSecret, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(default(JsonElement));
        var reader = new AiSettingsReader(settings, Substitute.For<ICurrentUser>(), NullLogger<AiSettingsReader>.Instance);
        return new SpeechToTextRouter(reader, Transcriber);
    }
}
