/**
 * Dictation goes to the server as 16 kHz mono 16-bit PCM WAV: the server takes the silence out before any of it is sent
 * to the speech service, which it cannot do in compressed WebM or Ogg. The browser records compressed audio
 * (MediaRecorder), so the clip is decoded and converted here before upload.
 */

/** Speech services work at 16 kHz; anything higher only makes the upload larger. */
export const SPEECH_SAMPLE_RATE = 16_000;

/** A mono 16-bit PCM WAV of `samples` (each from -1 to 1), at `sampleRate`. */
export function encodeWav(samples: Float32Array, sampleRate: number): ArrayBuffer {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const text = (offset: number, value: string) => {
    for (let i = 0; i < value.length; i++) {
      view.setUint8(offset + i, value.charCodeAt(i));
    }
  };
  text(0, 'RIFF');
  view.setUint32(4, 36 + samples.length * 2, true);
  text(8, 'WAVE');
  text(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  text(36, 'data');
  view.setUint32(40, samples.length * 2, true);
  samples.forEach((sample, i) => {
    const clamped = Math.max(-1, Math.min(1, sample));
    view.setInt16(44 + i * 2, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true);
  });
  return buffer;
}

/** The clip as 16 kHz mono WAV; a clip that already is WAV is sent as it is (the Desktop records WAV). */
export async function toSpeechWav(audio: Blob): Promise<Blob> {
  if (audio.type.startsWith('audio/wav')) {
    return audio;
  }
  const decoder = new AudioContext();
  try {
    const decoded = await decoder.decodeAudioData(await audio.arrayBuffer());
    const frames = Math.max(1, Math.ceil(decoded.duration * SPEECH_SAMPLE_RATE));
    const mono = new OfflineAudioContext(1, frames, SPEECH_SAMPLE_RATE);
    const source = mono.createBufferSource();
    source.buffer = decoded;
    source.connect(mono.destination);
    source.start();
    const rendered = await mono.startRendering();
    return new Blob([encodeWav(rendered.getChannelData(0), SPEECH_SAMPLE_RATE)], { type: 'audio/wav' });
  } finally {
    void decoder.close();
  }
}
