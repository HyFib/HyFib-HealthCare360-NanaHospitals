using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The speech-to-text service of this run (Whisper-compatible): it keeps the audio each request carried, so a test can
/// see exactly what left the server, and answers with a fixed transcript. Nothing goes to the network.
/// </summary>
public sealed class FakeSpeechService
{
    /// <summary>The address the speech set-up of a test names.</summary>
    public const string BaseUrl = "https://speech.test/v1";

    private readonly ConcurrentQueue<byte[]> _uploads = new();

    /// <summary>The audio file of every transcription request, oldest first.</summary>
    public IReadOnlyList<byte[]> Uploads => [.. _uploads];

    public void Clear() => _uploads.Clear();

    public HttpMessageHandler Handler() => new Forward(this);

    private sealed class Forward(FakeSpeechService service) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form)
                {
                    if (part.Headers.ContentDisposition?.Name?.Trim('"') == "file")
                    {
                        service._uploads.Enqueue(await part.ReadAsByteArrayAsync(cancellationToken));
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Fever for two days"}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
