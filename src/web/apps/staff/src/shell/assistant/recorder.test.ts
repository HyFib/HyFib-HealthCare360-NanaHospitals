import { describe, expect, it } from 'vitest';
import type { AiStatusDto } from './api';
import {
  MAX_AUDIO_BYTES,
  MAX_RECORDING_MS,
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
});

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
    expect(MAX_RECORDING_MS).toBe(30_000);
    expect(shouldStopRecording(29_999, 1000)).toBeNull();
    expect(shouldStopRecording(30_000, 1000)).toBe('time');
  });

  it('stops before the 10 MB upload limit', () => {
    expect(MAX_AUDIO_BYTES).toBe(10 * 1024 * 1024);
    expect(STOP_AT_BYTES).toBeLessThan(MAX_AUDIO_BYTES);
    expect(shouldStopRecording(1000, STOP_AT_BYTES - 1)).toBeNull();
    expect(shouldStopRecording(1000, STOP_AT_BYTES)).toBe('size');
  });

  it('refuses empty and oversized recordings', () => {
    expect(recordingProblem(0)).toContain('Nothing was recorded');
    expect(recordingProblem(MAX_AUDIO_BYTES + 1)).toContain('too large');
    expect(recordingProblem(120_000)).toBeNull();
    expect(recordingProblem(MAX_AUDIO_BYTES)).toBeNull();
  });

  it('explains microphone failures', () => {
    expect(microphoneProblem('NotAllowedError')).toContain('Allow microphone access');
    expect(microphoneProblem('NotFoundError')).toContain('No microphone');
    expect(microphoneProblem('NotReadableError')).toContain('in use');
    expect(microphoneProblem(undefined)).toContain('could not be started');
  });
});

describe('voiceAvailability', () => {
  it('always offers Desktop speech in HC360 Desktop', () => {
    expect(voiceAvailability({ isDesktop: true, recorderSupported: false, status: undefined, statusFailed: true })).toEqual({ mode: 'desktop', reason: null });
  });

  it('offers browser recording only with server speech-to-text', () => {
    expect(voiceAvailability({ isDesktop: false, recorderSupported: true, status: status(true), statusFailed: false })).toEqual({ mode: 'server', reason: null });
    const off = voiceAvailability({ isDesktop: false, recorderSupported: true, status: status(false), statusFailed: false });
    expect(off.mode).toBeNull();
    expect(off.reason).toContain('server speech-to-text');
  });

  it('explains an unsupported browser or a failed check, and says nothing while checking', () => {
    expect(voiceAvailability({ isDesktop: false, recorderSupported: false, status: status(true), statusFailed: false }).reason).toContain('not supported');
    expect(voiceAvailability({ isDesktop: false, recorderSupported: true, status: undefined, statusFailed: true }).reason).toContain('could not be checked');
    expect(voiceAvailability({ isDesktop: false, recorderSupported: true, status: undefined, statusFailed: false })).toEqual({ mode: null, reason: null });
  });
});
