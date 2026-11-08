using System.Buffers.Binary;
using HyFib.HealthCare360.Modules.AI.Services.Speech;

namespace HyFib.HealthCare360.Modules.AI.Tests;

/// <summary>What the server does to dictation before it is sent: read the WAV, keep the speech, write a plain WAV back.</summary>
public sealed class PcmAudioTests
{
    private const int Rate = 16_000;

    private static short[] Hiss(double seconds) =>
        [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (short)((i * 7919 % 81) - 40))];

    private static short[] Tone(double seconds, short amplitude = 8000) =>
        [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (short)(amplitude * Math.Sin(2 * Math.PI * 220 * i / Rate)))];

    [Fact]
    public void Silence_is_taken_out_and_speech_kept_with_a_margin()
    {
        var clip = new PcmClip(Rate, [.. Hiss(2), .. Tone(1), .. Hiss(1), .. Tone(0.5), .. Hiss(2)]);

        var spoken = PcmAudio.StripSilence(clip);

        // 1.5 s of speech; each stretch keeps up to 200 ms either side (the 1 s pause between them is mostly dropped).
        spoken.Duration.TotalSeconds.ShouldBeInRange(1.5, 1.5 + (4 * 0.2) + 0.02);
    }

    [Fact]
    public void A_recording_without_speech_keeps_nothing() =>
        PcmAudio.StripSilence(new PcmClip(Rate, Hiss(3))).Samples.ShouldBeEmpty();

    [Fact]
    public void A_quiet_speaker_in_a_quiet_room_is_still_heard() =>
        PcmAudio.StripSilence(new PcmClip(Rate, [.. Hiss(1), .. Tone(1, amplitude: 600), .. Hiss(1)])).Duration.TotalSeconds.ShouldBeGreaterThan(0.9);

    [Fact]
    public void A_stereo_wav_is_read_as_one_channel_and_written_back_plain()
    {
        // Stereo, 16-bit, with a LIST chunk (recorder metadata) before the samples.
        var samples = new short[] { 100, 300, -200, -400 };
        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        }

        var wav = new List<byte>();
        wav.AddRange("RIFF"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(0));
        wav.AddRange("WAVE"u8.ToArray());
        wav.AddRange("fmt "u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(16));
        wav.AddRange(BitConverter.GetBytes((short)1));
        wav.AddRange(BitConverter.GetBytes((short)2));
        wav.AddRange(BitConverter.GetBytes(44_100));
        wav.AddRange(BitConverter.GetBytes(44_100 * 4));
        wav.AddRange(BitConverter.GetBytes((short)4));
        wav.AddRange(BitConverter.GetBytes((short)16));
        wav.AddRange("LIST"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(4));
        wav.AddRange("INFO"u8.ToArray());
        wav.AddRange("data"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(data.Length));
        wav.AddRange(data);

        PcmAudio.TryRead([.. wav], out var clip, out _).ShouldBeTrue();
        (clip.SampleRate, clip.Samples).ShouldBe((44_100, new short[] { 200, -300 }));

        var plain = PcmAudio.ToWav(clip);
        plain.Length.ShouldBe(44 + 4, "no chunk but the format and the samples: the recorder's metadata is not sent");
        PcmAudio.TryRead(plain, out var back, out _).ShouldBeTrue();
        back.ShouldBe(clip with { Samples = back.Samples });
        back.Samples.ShouldBe(clip.Samples);
    }

    [Theory]
    [InlineData(3, 8)] // IEEE float
    [InlineData(1, 8)] // 8-bit PCM
    public void Only_16_bit_pcm_is_read(short format, short bits)
    {
        var wav = PcmAudio.ToWav(new PcmClip(Rate, [1, 2, 3]));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), format);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), bits);
        PcmAudio.TryRead(wav, out _, out var problem).ShouldBeFalse();
        problem.ShouldNotBeEmpty();
    }

    [Fact]
    public void Compressed_audio_is_not_read()
    {
        PcmAudio.TryRead([0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0], out _, out _).ShouldBeFalse();
        PcmAudio.TryRead("OggS"u8, out _, out _).ShouldBeFalse();
    }
}
