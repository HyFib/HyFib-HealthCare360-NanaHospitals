import { describe, expect, it } from 'vitest';
import type { AiStatusDto } from '@/lib/ai';
import {
  MAX_AUDIO_BYTES,
  microphoneProblem,
  pickRecordingType,
  recordingProblem,
  shouldStopRecording,
  STOP_AT_BYTES,
  uploadTypeFor,
  voiceAvailability,
} from './recorder';

const status = (serverSpeechToText: boolean): AiStatusDto => ({
  enabled: false,
  provider: 'anthropic',
  model: null,
  reason: 'AI features are turned off for this facility (setting ai.enabled).',
  serverSpeechToText,
  speechProvider: serverSpeechToText ? 'whisper' : 'none',
  speechReason: serverSpeechToText ? null : 'Server speech-to-text is not configured.',
  timeoutSeconds: 90,
});

// The assistant's use; clinical dictation's (90-second takes) is tested with it.
const use = { maxRecordingMs: 30_000, typeInstead: 'type the command' };

describe('recording format', () => {
  it('prefers WebM Opus, then Ogg, and refuses formats the server does not accept', () => {
    expect(pickRecordingType(() => true)).toBe('audio/webm;codecs=opus');
    expect(pickRecordingType((type) => type.startsWith('audio/ogg'))).toBe('audio/ogg;codecs=opus');
    expect(pickRecordingType((type) => type === 'audio/mp4')).toBeNull();
  });

  it('uploads with the plain media type the server checks against the file signature', () => {
    expect(uploadTypeFor('audio/webm;codecs=opus')).toEqual({ contentType: 'audio/webm', extension: '.webm' });
    expect(uploadTypeFor('video/webm')).toEqual({ contentType: 'audio/webm', extension: '.webm' });
    expect(uploadTypeFor('audio/ogg; codecs=opus')).toEqual({ contentType: 'audio/ogg', extension: '.ogg' });
    expect(uploadTypeFor('audio/mp4')).toBeNull();
  });
});

describe('recording guards', () => {
  it('stops after 30 seconds', () => {
    expect(shouldStopRecording(29_999, 1000, use.maxRecordingMs)).toBeNull();
    expect(shouldStopRecording(30_000, 1000, use.maxRecordingMs)).toBe('time');
    expect(shouldStopRecording(30_000, 1000, 90_000)).toBeNull();
  });

  it('stops before the 10 MB upload limit', () => {
    expect(MAX_AUDIO_BYTES).toBe(10 * 1024 * 1024);
    expect(STOP_AT_BYTES).toBeLessThan(MAX_AUDIO_BYTES);
    expect(shouldStopRecording(1000, STOP_AT_BYTES - 1, 90_000)).toBeNull();
    expect(shouldStopRecording(1000, STOP_AT_BYTES, 90_000)).toBe('size');
  });

  it('refuses empty and oversized recordings', () => {
    expect(recordingProblem(0, 30_000)).toContain('Nothing was recorded');
    expect(recordingProblem(MAX_AUDIO_BYTES + 1, 30_000)).toContain('too large');
    expect(recordingProblem(120_000, 30_000)).toBeNull();
    expect(recordingProblem(MAX_AUDIO_BYTES, 30_000)).toBeNull();
    expect(recordingProblem(MAX_AUDIO_BYTES + 1, 90_000)).toContain('Keep it under 90 seconds.');
  });

  it('explains microphone failures', () => {
    expect(microphoneProblem('NotAllowedError', use.typeInstead)).toContain('Allow microphone access');
    expect(microphoneProblem('NotFoundError', use.typeInstead)).toContain('No microphone');
    expect(microphoneProblem('NotReadableError', use.typeInstead)).toContain('in use');
    expect(microphoneProblem(undefined, use.typeInstead)).toContain('could not be started');
  });
});

describe('voiceAvailability', () => {
  it('always offers Desktop speech in HC360 Desktop', () => {
    expect(
      voiceAvailability({ isDesktop: true, recorderSupported: false, status: undefined, statusFailed: true }, use),
    ).toEqual({ mode: 'desktop', reason: null });
  });

  it('offers browser recording only with server speech-to-text', () => {
    expect(
      voiceAvailability({ isDesktop: false, recorderSupported: true, status: status(true), statusFailed: false }, use),
    ).toEqual({ mode: 'server', reason: null });
    const off = voiceAvailability(
      {
        isDesktop: false,
        recorderSupported: true,
        status: status(false),
        statusFailed: false,
      },
      use,
    );
    expect(off.mode).toBeNull();
    expect(off.reason).toContain('server speech-to-text');
  });

  it('explains an unsupported browser or a failed check, and says nothing while checking', () => {
    expect(
      voiceAvailability({ isDesktop: false, recorderSupported: false, status: status(true), statusFailed: false }, use)
        .reason,
    ).toContain('not supported');
    expect(
      voiceAvailability({ isDesktop: false, recorderSupported: true, status: undefined, statusFailed: true }, use)
        .reason,
    ).toContain('could not be checked');
    expect(
      voiceAvailability({ isDesktop: false, recorderSupported: true, status: undefined, statusFailed: false }, use),
    ).toEqual({ mode: null, reason: null });
  });
});
