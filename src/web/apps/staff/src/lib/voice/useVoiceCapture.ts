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
  /**
   * Abandons the capture (panel closed): a browser recording is stopped and discarded, also one whose microphone is
   * still opening, and text or a failure that arrives later is dropped. Desktop dictation cannot be interrupted from
   * the page; its text is dropped.
   */
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
  // The capture whose outcome is still wanted. Cancelling moves it on, so a microphone that opens after the panel
  // closed (the browser was still asking for permission) is closed again at once, and late text is dropped.
  const wanted = useRef(0);
  const maxSeconds = Math.round(use.maxRecordingMs / 1000);

  const abandon = () => {
    wanted.current += 1;
    recording.current?.cancel();
    recording.current = null;
  };

  // Release the microphone if the panel goes away mid-recording (sign-out, lock).
  const abandonOnUnmount = useEffectEvent(abandon);
  useEffect(() => () => abandonOnUnmount(), []);

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

  const dictateOnDesktop = (capture: number) => {
    const isWanted = () => capture === wanted.current;
    setPhase('listening');
    bridge
      .dictate('en-IN')
      .then((result) => {
        if (isWanted()) {
          deliver(result.text);
        }
      })
      .catch(() => {
        if (isWanted()) {
          setError('Voice input did not work. Check the microphone and try again, or type instead.');
        }
      })
      .finally(() => {
        if (isWanted()) {
          setPhase('idle');
        }
      });
  };

  const recordAndTranscribe = async (capture: number) => {
    const isWanted = () => capture === wanted.current;
    setPhase('starting');
    try {
      const session = await startRecording(use);
      if (!isWanted()) {
        session.cancel();
        return;
      }
      recording.current = session;
      const started = Date.now();
      setStartedAt(started);
      setNow(started);
      setPhase('listening');
      const clip = await session.result;
      if (!clip || !isWanted()) {
        return;
      }
      recording.current = null;
      setPhase('transcribing');
      const response = await transcribeAudio(clip.blob, clip.fileName, patientId);
      if (isWanted()) {
        deliver(response.text);
      }
    } catch (problem) {
      if (isWanted()) {
        setError(problem instanceof VoiceError ? problem.message : use.describeError(problem));
      }
    } finally {
      // A later capture owns the recording and the phase once this one has been abandoned.
      if (isWanted()) {
        recording.current = null;
        setPhase('idle');
      }
    }
  };

  const start = () => {
    if (!mode || phase !== 'idle') {
      return;
    }
    setError(null);
    const capture = ++wanted.current;
    if (mode === 'desktop') {
      dictateOnDesktop(capture);
    } else {
      void recordAndTranscribe(capture);
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
    cancel: () => {
      abandon();
      setPhase('idle');
    },
  };
}
