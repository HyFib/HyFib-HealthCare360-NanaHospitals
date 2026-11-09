import { describe, expect, it } from 'vitest';
import { encodeWav, SPEECH_SAMPLE_RATE, toSpeechWav } from './wav';

describe('encodeWav', () => {
  it('writes a mono 16-bit PCM WAV the server can read', () => {
    const wav = new DataView(encodeWav(new Float32Array([0, 1, -1, 0.5, 2]), SPEECH_SAMPLE_RATE));
    const text = (offset: number) => String.fromCharCode(...[0, 1, 2, 3].map((i) => wav.getUint8(offset + i)));
    expect(text(0)).toBe('RIFF');
    expect(text(8)).toBe('WAVE');
    expect(wav.getUint16(20, true)).toBe(1); // PCM
    expect(wav.getUint16(22, true)).toBe(1); // mono
    expect(wav.getUint32(24, true)).toBe(16_000);
    expect(wav.getUint16(34, true)).toBe(16);
    expect(wav.getUint32(40, true)).toBe(10);
    expect([0, 1, 2, 3, 4].map((i) => wav.getInt16(44 + i * 2, true))).toEqual([0, 32767, -32768, 16383, 32767]);
  });

  it('sends a clip that already is WAV as it is', async () => {
    const clip = new Blob([new Uint8Array(4)], { type: 'audio/wav' });
    expect(await toSpeechWav(clip)).toBe(clip);
  });
});
