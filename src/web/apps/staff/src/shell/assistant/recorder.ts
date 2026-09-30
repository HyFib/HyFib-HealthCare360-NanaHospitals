import type { AiStatusDto } from './api';

/**
 * Voice capture for the assistant. HC360 Desktop dictates with offline Windows speech (`bridge.dictate`); a plain
 * browser records WebM/Opus (or Ogg) with MediaRecorder and sends it to the server's speech-to-text, which accepts
 * WebM, WAV or Ogg of at most 10 MB. The pure guards below are unit-tested; `startRecording` needs a real browser.
 */

/** Recording stops by itself after this long (a spoken command takes a few seconds). */
export const MAX_RECORDING_MS = 30_000;
/** The server's limit for one upload (AudioFormat.MaxBytes). */
export const MAX_AUDIO_BYTES = 10 * 1024 * 1024;
/** Stop a little before the limit so the multipart request always fits. */
export const STOP_AT_BYTES = MAX_AUDIO_BYTES - 256 * 1024;

/** Preferred MediaRecorder formats, best first (Chromium/Edge: WebM Opus; Firefox: Ogg Opus). */
export const RECORDING_TYPES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/ogg'] as const;

export type VoiceMode = 'desktop' | 'server';

const COULD_NOT_RECORD = 'Recording could not start in this browser. Try again, or type the command.';

export class VoiceError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'VoiceError';
  }
}

/** The first format the browser can record, or null (Safari records only MP4, which the server does not accept). */
export function pickRecordingType(isSupported: (type: string) => boolean): string | null {
  return RECORDING_TYPES.find((type) => isSupported(type)) ?? null;
}

/** Content type and file extension for the upload ("audio/webm;codecs=opus" → audio/webm, .webm). */
export function uploadTypeFor(
  recordingType: string,
): { contentType: 'audio/webm' | 'audio/ogg'; extension: '.webm' | '.ogg' } | null {
  const mediaType = recordingType.split(';', 1)[0]?.trim().toLowerCase();
  if (mediaType === 'audio/webm' || mediaType === 'video/webm') {
    return { contentType: 'audio/webm', extension: '.webm' };
  }
  if (mediaType === 'audio/ogg') {
    return { contentType: 'audio/ogg', extension: '.ogg' };
  }
  return null;
}

/** Why recording must stop now, if it must: the 30-second limit or the upload size limit. */
export function shouldStopRecording(elapsedMs: number, bytes: number): 'time' | 'size' | null {
  if (bytes >= STOP_AT_BYTES) {
    return 'size';
  }
  return elapsedMs >= MAX_RECORDING_MS ? 'time' : null;
}

/** A recording the server would refuse, explained; null when it can be sent. */
export function recordingProblem(bytes: number): string | null {
  if (bytes <= 0) {
    return 'Nothing was recorded. Check that the microphone is connected and not muted.';
  }
  if (bytes > MAX_AUDIO_BYTES) {
    return 'The recording is too large to send. Keep it under 30 seconds.';
  }
  return null;
}

/** Microphone access failures (DOMException names) in plain language. */
export function microphoneProblem(errorName: string | null | undefined): string {
  switch (errorName) {
    case 'NotAllowedError':
    case 'SecurityError':
      return 'The browser blocked the microphone. Allow microphone access for this site, then try again.';
    case 'NotFoundError':
    case 'OverconstrainedError':
      return 'No microphone was found. Connect one, or check the sound settings.';
    case 'NotReadableError':
    case 'AbortError':
      return 'The microphone is in use by another program. Close it and try again.';
    default:
      return 'The microphone could not be started. Try again, or type the command.';
  }
}

export interface VoiceAvailability {
  mode: VoiceMode | null;
  /** Why voice is not offered (shown under the text box). */
  reason: string | null;
}

/** Whether the mic is offered, and how: Desktop speech first, else server speech-to-text when it is set up. */
export function voiceAvailability(input: {
  isDesktop: boolean;
  recorderSupported: boolean;
  status: AiStatusDto | undefined;
  statusFailed: boolean;
}): VoiceAvailability {
  if (input.isDesktop) {
    return { mode: 'desktop', reason: null };
  }
  if (!input.recorderSupported) {
    return {
      mode: null,
      reason: 'Voice input is not supported in this browser. Type the command, or use HC360 Desktop.',
    };
  }
  if (!input.status) {
    return {
      mode: null,
      reason: input.statusFailed ? 'Voice input could not be checked right now. Type the command instead.' : null,
    };
  }
  if (input.status.serverSpeechToText) {
    return { mode: 'server', reason: null };
  }
  return {
    mode: null,
    reason:
      'Voice input in a browser needs server speech-to-text, which is not set up. Use HC360 Desktop, or type the command.',
  };
}

/** True when this browser can record audio at all (secure context, microphone API and MediaRecorder). */
export function recorderSupported(): boolean {
  return (
    typeof window !== 'undefined' &&
    window.isSecureContext &&
    typeof navigator !== 'undefined' &&
    typeof navigator.mediaDevices?.getUserMedia === 'function' &&
    typeof MediaRecorder !== 'undefined' &&
    pickRecordingType((type) => MediaRecorder.isTypeSupported(type)) !== null
  );
}

export interface AudioClip {
  blob: Blob;
  fileName: string;
}

export interface Recording {
  /** Stops recording; `result` then resolves with the audio. */
  stop: () => void;
  /** Stops and throws the audio away (the panel was closed). */
  cancel: () => void;
  /** The recorded audio, or null when cancelled. Rejects with a VoiceError. */
  result: Promise<AudioClip | null>;
}

/**
 * Records from the microphone until `stop()`, 30 seconds, or the size limit. The microphone is released as soon as
 * recording ends. The audio stays in memory and is only sent for transcription.
 */
export async function startRecording(): Promise<Recording> {
  const recordingType = recorderSupported() ? pickRecordingType((type) => MediaRecorder.isTypeSupported(type)) : null;
  const upload = recordingType ? uploadTypeFor(recordingType) : null;
  if (!recordingType || !upload) {
    throw new VoiceError(
      'This browser cannot record voice in a format the server understands. Use Edge or Chrome, or HC360 Desktop.',
    );
  }

  let stream: MediaStream;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
  } catch (error) {
    throw new VoiceError(microphoneProblem(error instanceof DOMException ? error.name : null));
  }

  let recorder: MediaRecorder;
  try {
    recorder = new MediaRecorder(stream, { mimeType: recordingType, audioBitsPerSecond: 32_000 });
  } catch {
    stream.getTracks().forEach((track) => track.stop());
    throw new VoiceError(COULD_NOT_RECORD);
  }
  const chunks: Blob[] = [];
  const startedAt = Date.now();
  let bytes = 0;
  let cancelled = false;
  const stopIfRecording = () => {
    if (recorder.state !== 'inactive') {
      recorder.stop();
    }
  };
  // The 30-second limit; recording starts a moment later, below.
  const timer = setTimeout(stopIfRecording, MAX_RECORDING_MS);
  const release = () => {
    clearTimeout(timer);
    stream.getTracks().forEach((track) => track.stop());
  };

  const result = new Promise<AudioClip | null>((resolve, reject) => {
    recorder.ondataavailable = (event) => {
      if (event.data.size > 0) {
        chunks.push(event.data);
        bytes += event.data.size;
      }
      if (shouldStopRecording(Date.now() - startedAt, bytes)) {
        stopIfRecording();
      }
    };
    recorder.onstop = () => {
      release();
      if (cancelled) {
        resolve(null);
        return;
      }
      const problem = recordingProblem(bytes);
      if (problem) {
        reject(new VoiceError(problem));
        return;
      }
      resolve({ blob: new Blob(chunks, { type: upload.contentType }), fileName: `voice${upload.extension}` });
    };
    recorder.onerror = () => {
      release();
      reject(new VoiceError('Recording stopped unexpectedly. Please try again.'));
    };
  });

  try {
    // A chunk every second lets the size guard act during long recordings.
    recorder.start(1000);
  } catch {
    release();
    throw new VoiceError(COULD_NOT_RECORD);
  }

  return {
    stop: stopIfRecording,
    cancel: () => {
      cancelled = true;
      if (recorder.state === 'inactive') {
        release();
      } else {
        recorder.stop();
      }
    },
    result,
  };
}
