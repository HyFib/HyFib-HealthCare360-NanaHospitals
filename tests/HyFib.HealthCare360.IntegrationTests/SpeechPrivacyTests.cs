using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.AI.Features.Voice;
using HyFib.HealthCare360.Modules.AI.Services.Speech;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Server speech-to-text sends no raw audio: nothing at all until the facility's AI processing agreement is recorded
/// (a guarded setting a second administrator approves), then only the speech, silence taken out, at most two minutes of
/// it, as a plain mono WAV. The fake speech service keeps exactly what left the server.
/// </summary>
public sealed class SpeechPrivacyTests(HospitalServer server)
{
    private const string Transcribe = "/api/v1/ai/voice/transcribe";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dictation_leaves_only_as_speech_and_only_under_the_facilitys_agreement()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var facilityId = (await doctor.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId;
        await SetAsync("ai.enabled", true, null);
        await SetAsync("ai.speech", new { provider = "whisper", url = FakeSpeechService.BaseUrl, model = "whisper-1", language = "en" }, null);
        server.Speech.Clear();
        try
        {
            // Without the agreement: refused, nothing sent, and the screens are told dictation is not offered.
            var refused = await PostAsync(doctor, Wav(Silence(1), Tone(2), Silence(3)));
            refused.Status.ShouldBe(HttpStatusCode.Conflict);
            refused.Body.GetProperty("code").GetString().ShouldBe("ai.no_processing_agreement");
            server.Speech.Uploads.ShouldBeEmpty();
            var status = await doctor.GetAsync<JsonElement>("/api/v1/ai/status");
            status.GetProperty("serverSpeechToText").GetBoolean().ShouldBeFalse();

            // Turning the agreement on is loosening a guarded setting: it waits for a second administrator.
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            ApprovalRequestDto request;
            using (var asked = await admin.SendRawAsync(HttpMethod.Put, "/api/v1/settings/ai.processingAgreement",
                       new SaveSettingRequest(JsonValue.Create(true), facilityId, false, null, null, "Agreement signed with the speech service.")))
            {
                asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
                request = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
                request.Kind.ShouldBe(SettingGuard.ChangeKind);
            }

            (await PostAsync(doctor, Wav(Tone(1)))).Status.ShouldBe(HttpStatusCode.Conflict, "nothing is sent while the agreement waits");
            await admin.PostAsync($"/api/v1/approvals/{request.Id}/withdraw", new WithdrawApprovalRequest(request.Version));
            await SetAsync("ai.processingAgreement", true, facilityId);

            // One second of speech between four seconds of silence: only the speech (with 200 ms either side) leaves.
            var heard = await PostAsync(doctor, Wav(Silence(2), Tone(1), Silence(2)));
            heard.Status.ShouldBe(HttpStatusCode.OK);
            heard.Body.GetProperty("text").GetString().ShouldBe("Fever for two days");
            var sent = server.Speech.Uploads.ShouldHaveSingleItem();
            PcmAudio.TryRead(sent, out var clip, out _).ShouldBeTrue();
            clip.Duration.TotalSeconds.ShouldBeInRange(1.0, 1.5);
            clip.Samples.Take(clip.SampleRate / 10).ShouldNotContain(s => Math.Abs((int)s) > 3000, "the margin before the speech is room tone, not more speech");
            sent.Length.ShouldBe(44 + (clip.Samples.Length * 2), "a plain WAV: the format and the samples, no other chunks");
            heard.Body.GetProperty("sentMs").GetInt32().ShouldBeInRange(1000, 1500);

            // Silence alone is not sent at all.
            var quiet = await PostAsync(doctor, Wav(Silence(3)));
            quiet.Status.ShouldBe(HttpStatusCode.OK);
            quiet.Body.GetProperty("text").GetString().ShouldBeEmpty();
            server.Speech.Uploads.Count.ShouldBe(1);

            // More than two minutes of speech is refused before anything is sent.
            var tooLong = await PostAsync(doctor, Wav(Tone(125)));
            tooLong.Status.ShouldBe(HttpStatusCode.BadRequest);
            server.Speech.Uploads.Count.ShouldBe(1);

            // Compressed audio cannot be looked inside, so it is refused rather than sent.
            var webm = new byte[64];
            new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.CopyTo(webm, 0);
            (await PostAsync(doctor, webm, "audio/webm")).Status.ShouldBe(HttpStatusCode.BadRequest);
            server.Speech.Uploads.Count.ShouldBe(1);
        }
        finally
        {
            await RemoveAsync("ai.processingAgreement", facilityId);
            await RemoveAsync("ai.speech", null);
            await RemoveAsync("ai.enabled", null);
        }
    }

    private const int Rate = 16_000;

    private static short[] Silence(double seconds)
    {
        // Room tone: a faint hiss, as a real microphone records between words.
        var random = new Random(7);
        return [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(_ => (short)random.Next(-40, 40))];
    }

    private static short[] Tone(double seconds) =>
        [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / Rate)))];

    private static byte[] Wav(params short[][] parts) => PcmAudio.ToWav(new PcmClip(Rate, [.. parts.SelectMany(p => p)]));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(ApiClient client, byte[] audio, string type = "audio/wav")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "audio", type == "audio/wav" ? "voice.wav" : "voice.webm");
        using var response = await client.SendContentAsync(HttpMethod.Post, Transcribe, form);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task SetAsync<T>(string key, T value, Guid? facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(key, value, facilityId, cancellationToken: Ct);
    }

    private async Task RemoveAsync(string key, Guid? facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(key, facilityId, Ct);
    }
}
