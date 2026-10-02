import { bridge } from '@hc360/core';
import { useEffect, useEffectEvent, useRef, useState } from 'react';
import { transcribeAudio } from '@/lib/ai';
import { startRecording, VoiceError, type Recording, type VoiceMode, type VoiceUse } from './recorder';

export type VoicePhase = 'idle' | 'starting' | 'listening' | 'transcribing';

export interface VoiceCapture {
  phase: VoicePhase;
  error: string | null;
  /** Seconds recorded so far (browser recording). */
  seconds: number;
  /** The caller's limit for one browser recording, in seconds. */
  maxSeconds: number;
  start: () => void;
  /** Ends a browser recording early; the audio is then transcribed. */
  stop: () => void;
  /** Stops and discards a browser recording (panel closed). Desktop dictation cannot be interrupted from the page. */
  cancel: () => void;
}

const NOTHING_HEARD = 'Nothing was heard. Try again, a little closer to the microphone.';

/**
 * Speech to text for the assistant and clinical dictation: HC360 Desktop's offline Windows speech
 * (`bridge.dictate('en-IN')`), or a browser recording sent to the server's speech-to-text. The recognised text goes to
 * `onText` for the user to check and edit; it is never logged or stored. `use` sets the recording limit and the
 * wording of failures for the screen.
 */
export function useVoiceCapture(
  mode: VoiceMode | null,
  patientId: string | null,
  onText: (text: string) => void,
  use: VoiceUse,
): VoiceCapture {
  const [phase, setPhase] = useState<VoicePhase>('idle');
  const [error, setError] = useState<string | null>(null);
  const [startedAt, setStartedAt] = useState(0);
  const [now, setNow] = useState(0);
  const recording = useRef<Recording | null>(null);
  const maxSeconds = Math.round(use.maxRecordingMs / 1000);

  // Release the microphone if the panel goes away mid-recording (sign-out, lock).
  const discardRecording = useEffectEvent(() => recording.current?.cancel());
  useEffect(() => () => discardRecording(), []);

  // A ticking clock while the browser records, for "12 s of 30".
  useEffect(() => {
    if (phase !== 'listening' || mode !== 'server') {
      return;
    }
    const timer = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(timer);
  }, [phase, mode]);

  const deliver = (text: string | null | undefined) => {
    const heard = (text ?? '').trim();
    if (heard) {
      onText(heard);
    } else {
      setError(NOTHING_HEARD);
    }
  };

  const dictateOnDesktop = () => {
    setPhase('listening');
    bridge
      .dictate('en-IN')
      .then((result) => deliver(result.text))
      .catch(() => setError('Voice input did not work. Check the microphone and try again, or type instead.'))
      .finally(() => setPhase('idle'));
  };

  const recordAndTranscribe = async () => {
    setPhase('starting');
    try {
      const session = await startRecording(use);
      recording.current = session;
      const started = Date.now();
      setStartedAt(started);
      setNow(started);
      setPhase('listening');
      const clip = await session.result;
      recording.current = null;
      if (!clip) {
        return;
      }
      setPhase('transcribing');
      const response = await transcribeAudio(clip.blob, clip.fileName, patientId);
      deliver(response.text);
    } catch (problem) {
      setError(problem instanceof VoiceError ? problem.message : use.describeError(problem));
    } finally {
      recording.current = null;
      setPhase('idle');
    }
  };

  const start = () => {
    if (!mode || phase !== 'idle') {
      return;
    }
    setError(null);
    if (mode === 'desktop') {
      dictateOnDesktop();
    } else {
      void recordAndTranscribe();
    }
  };

  const seconds =
    phase === 'listening' && mode === 'server' && startedAt > 0
      ? Math.min(maxSeconds, Math.max(0, Math.floor((now - startedAt) / 1000)))
      : 0;

  return {
    phase,
    error,
    seconds,
    maxSeconds,
    start,
    stop: () => recording.current?.stop(),
    cancel: () => recording.current?.cancel(),
  };
}
