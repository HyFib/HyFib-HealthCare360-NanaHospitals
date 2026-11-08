using System.Buffers.Binary;

namespace HyFib.HealthCare360.Modules.AI.Services.Speech;

/// <summary>A recording as 16-bit samples of one channel (several channels are averaged), at its own sample rate.</summary>
internal sealed record PcmClip(int SampleRate, short[] Samples)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

/// <summary>
/// What the server does to dictation before any of it leaves for the speech service: it reads the WAV (16-bit PCM, the
/// only format it can look inside; compressed WebM and Ogg cannot be inspected without a decoder, so they are refused),
/// keeps only the stretches where someone speaks with a little margin either side, and refuses to send more than
/// <see cref="MaxSpeech"/> of speech. A recording in which nobody speaks is not sent at all. What leaves is a mono
/// 16-bit WAV of the speech alone: no room noise or conversation in the pauses, no file metadata.
/// </summary>
internal static class PcmAudio
{
    /// <summary>The most speech one request sends (after silence is taken out).</summary>
    public static readonly TimeSpan MaxSpeech = TimeSpan.FromSeconds(120);

    /// <summary>Frames of 20 ms are judged speech or silence.</summary>
    public const int FrameMilliseconds = 20;

    /// <summary>Speech keeps 200 ms either side, so words are not clipped.</summary>
    public const int MarginFrames = 10;

    /// <summary>A frame quieter than this (about −46 dBFS, RMS as a fraction of full scale) is never speech.</summary>
    public const double SilenceFloor = 0.005;

    /// <summary>A frame is speech when it is this many times louder than the recording's background (its quietest tenth).</summary>
    public const double SpeechOverBackground = 4.0;

    public static bool TryRead(ReadOnlySpan<byte> wav, out PcmClip clip, out string problem)
    {
        clip = null!;
        problem = "The recording is not a WAV file of 16-bit PCM audio.";
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        int? channels = null, rate = null, bits = null;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4));
            var body = offset + 8;
            if (size > (uint)(wav.Length - body))
            {
                // A recorder that stopped before writing the sizes: take what is there.
                size = (uint)(wav.Length - body);
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                var format = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 14, 2));
                const ushort pcm = 1, extensible = 0xFFFE;
                if (format is not (pcm or extensible) || bits != 16 || channels is < 1 or > 8 || rate is < 8000 or > 192000)
                {
                    return false;
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels is not { } count || rate is not { } sampleRate)
                {
                    return false;
                }

                var frames = (int)(size / (uint)(2 * count));
                var samples = new short[frames];
                for (var i = 0; i < frames; i++)
                {
                    var sum = 0;
                    for (var c = 0; c < count; c++)
                    {
                        sum += BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(body + (((i * count) + c) * 2), 2));
                    }

                    samples[i] = (short)(sum / count);
                }

                clip = new PcmClip(sampleRate, samples);
                problem = string.Empty;
                return true;
            }

            offset = body + (int)size + (int)(size & 1);
        }

        return false;
    }

    /// <summary>
    /// The speech of a clip: each 20 ms frame louder than both <see cref="SilenceFloor"/> and
    /// <see cref="SpeechOverBackground"/> times the recording's background, with <see cref="MarginFrames"/> either side;
    /// everything else is dropped. Empty when nobody speaks.
    /// </summary>
    public static PcmClip StripSilence(PcmClip clip)
    {
        var frameLength = Math.Max(1, clip.SampleRate * FrameMilliseconds / 1000);
        var frameCount = (clip.Samples.Length + frameLength - 1) / frameLength;
        if (frameCount == 0)
        {
            return clip with { Samples = [] };
        }

        var levels = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            var start = f * frameLength;
            var end = Math.Min(start + frameLength, clip.Samples.Length);
            double sum = 0;
            for (var i = start; i < end; i++)
            {
                var s = clip.Samples[i] / 32768.0;
                sum += s * s;
            }

            levels[f] = Math.Sqrt(sum / (end - start));
        }

        var sorted = levels.Order().ToArray();
        var background = sorted[Math.Min(sorted.Length - 1, sorted.Length / 10)];
        var threshold = Math.Max(SilenceFloor, background * SpeechOverBackground);

        var keep = new bool[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            if (levels[f] < threshold)
            {
                continue;
            }

            for (var k = Math.Max(0, f - MarginFrames); k <= Math.Min(frameCount - 1, f + MarginFrames); k++)
            {
                keep[k] = true;
            }
        }

        var kept = new List<short>(clip.Samples.Length);
        for (var f = 0; f < frameCount; f++)
        {
            if (keep[f])
            {
                var start = f * frameLength;
                kept.AddRange(clip.Samples.AsSpan(start, Math.Min(frameLength, clip.Samples.Length - start)));
            }
        }

        return clip with { Samples = [.. kept] };
    }

    /// <summary>A mono 16-bit PCM WAV of the clip, with no chunks but the format and the samples.</summary>
    public static byte[] ToWav(PcmClip clip)
    {
        var dataBytes = clip.Samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)clip.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(clip.SampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        for (var i = 0; i < clip.Samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 2))..], clip.Samples[i]);
        }

        return wav;
    }
}
