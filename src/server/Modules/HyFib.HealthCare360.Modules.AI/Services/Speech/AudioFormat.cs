namespace HyFib.HealthCare360.Modules.AI.Services.Speech;

/// <summary>
/// Accepted dictation audio (webm/opus from browsers, wav from the Desktop, ogg). The declared content type must match
/// the file signature, so arbitrary uploads are never forwarded to the speech server.
/// </summary>
internal static class AudioFormat
{
    public const long MaxBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/webm"] = "audio/webm",
        ["video/webm"] = "audio/webm",
        ["audio/wav"] = "audio/wav",
        ["audio/x-wav"] = "audio/wav",
        ["audio/wave"] = "audio/wav",
        ["audio/vnd.wave"] = "audio/wav",
        ["audio/ogg"] = "audio/ogg",
    };

    /// <summary>Media type without parameters (<c>audio/webm;codecs=opus</c> → <c>audio/webm</c>), or null if not accepted.</summary>
    public static string? Normalize(string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        return mediaType is not null && Canonical.TryGetValue(mediaType, out var canonical) ? canonical : null;
    }

    public static bool MatchesSignature(string mediaType, ReadOnlySpan<byte> header) => mediaType switch
    {
        // EBML header.
        "audio/webm" => header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3,
        // "RIFF" .... "WAVE".
        "audio/wav" => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8),
        // "OggS".
        "audio/ogg" => header.Length >= 4 && header[..4].SequenceEqual("OggS"u8),
        _ => false,
    };

    public static string Extension(string mediaType) => mediaType switch
    {
        "audio/webm" => ".webm",
        "audio/wav" => ".wav",
        _ => ".ogg",
    };
}
